using System.ComponentModel;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Take ownership of live resources that match a declared name but carry no infra marker: tags, description, comment or .infra-owner object are written, and firewalls and SSH keys are renamed to <owner>-<name>. Nothing else is changed.")]
public sealed class AdoptCommand(IAnsiConsole console, ErrorConsole errors, ISecretResolver secrets, CloudServices cloud)
    : CloudCommand<AdoptSettings>(console, errors, secrets, cloud)
{
    protected override async Task<int> ExecuteAsync(PlanRun run, CloudInventory inventory, AdoptSettings settings, Interaction interaction, InfraLog log)
    {
        if (WriteProblems(inventory))
            return InfraExitCodes.Invalid;

        var selected = Select(Targets(inventory), settings.Names);
        var targets = selected.Where(target => target.Node is { } node && inventory.Matches[node.Id].Unowned is not null).ToList();
        foreach (var owned in selected.Where(target => !targets.Contains(target) && settings.Names.Length > 0))
            CliOutput.WriteUnwrappedLine(Console, $"  [grey]{Markup.Escape(owned.Label)}: nothing to adopt[/]");
        if (targets.Count == 0)
        {
            Console.MarkupLine("Nothing to adopt.");
            return InfraExitCodes.Success;
        }

        var marker = inventory.Marker;
        foreach (var target in targets)
            CliOutput.WriteUnwrappedLine(Console, $"  [yellow]~[/] {Markup.Escape(target.Label)}: {Markup.Escape(target.Handler.AdoptText(target.Live!, OwnerMark.For(target.Node!, marker), marker))}");

        if (!settings.Yes)
        {
            if (!interaction.IsInteractive)
                throw new UsageException("adopt needs confirmation; rerun with --yes to adopt without a prompt.");
            if (!interaction.Confirm($"Adopt {targets.Count} resource(s) into {marker.Owner}?"))
            {
                Console.MarkupLine("Adopt cancelled.");
                return InfraExitCodes.Invalid;
            }
        }

        var adopted = 0;
        foreach (var target in targets)
        {
            try
            {
                await target.Handler.MarkAsync(target.Session, target.Live!, OwnerMark.For(target.Node!, marker), CancellationToken.None);
                adopted++;
                CliOutput.WriteUnwrappedLine(Console, $"  [green]adopted[/] {Markup.Escape(target.Label)}");
            }
            catch (Exception exception) when (exception is CloudApiException or HttpRequestException or NotSupportedException)
            {
                log.Error("adopt.failed", $"{target.Label}: {exception.Message}");
                CliOutput.WriteUnwrappedLine(Console, $"  [red]failed[/] {Markup.Escape(target.Label)}: {Markup.Escape(exception.Message)}");
                return adopted > 0 ? InfraExitCodes.PartiallyApplied : InfraExitCodes.Invalid;
            }
        }
        Console.MarkupLine($"Adopted {adopted} resource(s).");
        return InfraExitCodes.Success;
    }
}
