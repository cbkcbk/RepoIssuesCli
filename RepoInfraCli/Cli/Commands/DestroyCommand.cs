using System.ComponentModel;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Delete the cloud resources that carry this project's ownership marker, found from markers alone (resources already removed from infra.luau included): records, firewalls, droplets, database clusters, buckets, SSH keys, then VPCs. Never touches protected or exported resources, infra.host or existing.* lookups.")]
public sealed class DestroyCommand(IAnsiConsole console, ErrorConsole errors, ISecretResolver secrets, CloudServices cloud)
    : CloudCommand<DestroySettings>(console, errors, secrets, cloud)
{
    protected override bool ScansAllKinds => true;

    protected override async Task<int> ExecuteAsync(PlanRun run, CloudInventory inventory, DestroySettings settings, Interaction interaction, InfraLog log)
    {
        if (WriteProblems(inventory))
            return InfraExitCodes.Invalid;

        var owner = inventory.Marker.Owner;
        var owned = Targets(inventory).Where(target => target.Live is { Mark: { } mark } && mark.Owner == owner && target.Node is not { Existing: true }).ToList();
        var selected = Select(owned, settings.Names).OrderBy(target => target.Handler.DestroyRank).ToList();
        var kept = selected.Where(target => target.Live!.Mark!.Keeps).ToList();
        var doomed = selected.Except(kept).ToList();

        if (settings.Names.Length > 0 && kept.Count > 0)
        {
            var error = string.Join("; ", kept.Select(target => $"{target.Label} is {target.Live!.Mark!.KeepReason}"));
            Report(settings, [], kept, $"{error}; no flag overrides protect or exported");
            return InfraExitCodes.Invalid;
        }

        if (!settings.Json)
        {
            foreach (var target in doomed)
                CliOutput.WriteUnwrappedLine(Console, $"  [red]-[/] {Markup.Escape(target.Label)}: delete {Markup.Escape(target.Live!.Id)}");
            foreach (var target in kept)
                CliOutput.WriteUnwrappedLine(Console, $"  [grey]{Markup.Escape(target.Label)}: kept ({target.Live!.Mark!.KeepReason})[/]");
        }
        if (doomed.Count == 0)
        {
            Report(settings, [], kept, null, "Nothing to destroy.");
            return InfraExitCodes.Success;
        }

        if (settings.Yes)
        {
            if (settings.Confirm != owner)
                throw new UsageException($"destroy --yes needs --confirm {owner}.");
        }
        else
        {
            if (!interaction.IsInteractive)
                throw new UsageException($"destroy needs confirmation; rerun with --yes --confirm {owner}.");
            var answer = Console.Prompt(new TextPrompt<string>($"Type [bold]{Markup.Escape(owner)}[/] to destroy {doomed.Count} resource(s):").AllowEmpty());
            if (answer.Trim() != owner)
            {
                Console.MarkupLine("Destroy cancelled.");
                return InfraExitCodes.Invalid;
            }
        }

        var destroyed = new List<CloudTarget>();
        foreach (var target in doomed)
        {
            try
            {
                await target.Handler.DeleteAsync(target.Session, target.Live!, CancellationToken.None);
                destroyed.Add(target);
                log.Info("destroy.deleted", $"deleted {target.Label} ({target.Live!.Id})");
                if (!settings.Json)
                    CliOutput.WriteUnwrappedLine(Console, $"  [green]deleted[/] {Markup.Escape(target.Label)}");
            }
            catch (Exception exception) when (exception is CloudApiException or HttpRequestException or NotSupportedException)
            {
                log.Error("destroy.failed", $"{target.Label}: {exception.Message}");
                Report(settings, destroyed, kept, $"{target.Label}: {exception.Message}");
                return destroyed.Count > 0 ? InfraExitCodes.PartiallyApplied : InfraExitCodes.Invalid;
            }
        }

        Report(settings, destroyed, kept, null, $"Destroyed {destroyed.Count} resource(s).");
        return InfraExitCodes.Success;
    }

    void Report(DestroySettings settings, IReadOnlyList<CloudTarget> destroyed, IReadOnlyList<CloudTarget> kept, string? error, string? summary = null)
    {
        if (settings.Json)
        {
            CliOutput.WriteJson(Console, CloudJson.Serialize(new DestroyView(
                error is null,
                [.. destroyed.Select(target => LiveResourceView.From(target, "deleted"))],
                [.. kept.Select(target => LiveResourceView.From(target, target.Live!.Mark!.KeepReason))],
                error)));
            return;
        }
        if (error is not null)
            CliOutput.WriteUnwrappedLine(Errors, $"[red]Error:[/] {Markup.Escape(error)}");
        if (summary is not null)
            Console.MarkupLine(Markup.Escape(summary));
    }
}
