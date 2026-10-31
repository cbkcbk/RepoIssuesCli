using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Show the live state of declared cloud resources and of resources this project still owns but no longer declares. --field prints one live value, such as a droplet's ipv4_address, for scripts.")]
public sealed class ShowCommand(IAnsiConsole console, ErrorConsole errors, ISecretResolver secrets, CloudServices cloud)
    : CloudCommand<ShowSettings>(console, errors, secrets, cloud)
{
    protected override bool ScansAllKinds => true;

    protected override Task<int> ExecuteAsync(PlanRun run, CloudInventory inventory, ShowSettings settings, Interaction interaction, InfraLog log)
    {
        WriteProblems(inventory);
        var targets = Targets(inventory);
        if (settings.Name is null)
        {
            if (settings.Field is not null)
                throw new UsageException("--field needs a resource name.");
            WriteAll(inventory, targets, settings.Json);
            return Task.FromResult(inventory.Problems.Count > 0 ? InfraExitCodes.Invalid : InfraExitCodes.Success);
        }

        var target = Select(targets, [settings.Name])[0];
        if (settings.Field is { } field)
        {
            var value = target.Live is { } live ? live.Outputs.GetValueOrDefault(field) ?? live.Fields.GetValueOrDefault(field) : null;
            if (value is null)
                throw new CloudTargetNotFoundException($"{target.Label} has no live value '{field}'" + (target.Live is null ? " (it does not exist yet)." : "."));
            if (settings.Json)
                CliOutput.WriteJson(Console, CloudJson.Serialize(value));
            else
                Console.Profile.Out.Writer.WriteLine(value);
            return Task.FromResult(InfraExitCodes.Success);
        }

        var view = LiveResourceView.From(target, Status(inventory, target));
        if (settings.Json)
        {
            CliOutput.WriteJson(Console, CloudJson.Serialize(view));
            return Task.FromResult(InfraExitCodes.Success);
        }

        CliOutput.WriteUnwrappedLine(Console, $"[bold]{Markup.Escape(target.Label)}[/] [grey]{Markup.Escape(view.Status)}[/]");
        var table = new Table().Border(TableBorder.Simple).AddColumn("Field").AddColumn("Value");
        if (view.Id is not null)
            table.AddRow("id", Markup.Escape(view.Id));
        if (view.Marker is not null)
            table.AddRow("marker", Markup.Escape(view.Marker));
        foreach (var (key, value) in view.Fields.Concat(view.Outputs.Where(output => output.Key != "id")).OrderBy(entry => entry.Key, StringComparer.Ordinal))
            table.AddRow(Markup.Escape(key), Markup.Escape(value));
        Console.Write(table);
        return Task.FromResult(InfraExitCodes.Success);
    }

    void WriteAll(CloudInventory inventory, IReadOnlyList<CloudTarget> targets, bool json)
    {
        var views = targets.Select(target => LiveResourceView.From(target, Status(inventory, target))).ToList();
        if (json)
        {
            CliOutput.WriteJson(Console, CloudJson.Serialize(new ShowView(views)));
            return;
        }
        if (views.Count == 0)
        {
            Console.MarkupLine("No cloud resources.");
            return;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumn("Provider").AddColumn("Kind").AddColumn("Name").AddColumn("Status").AddColumn("Id");
        foreach (var view in views)
            table.AddRow(Markup.Escape(view.Provider), Markup.Escape(view.Kind), Markup.Escape(view.Name), Markup.Escape(view.Status), Markup.Escape(view.Id ?? ""));
        Console.Write(table);
    }

    static string Status(CloudInventory inventory, CloudTarget target) => target.Node switch
    {
        null => $"not in {InfraWorkspace.EntryFileName}",
        { Reference: { } owner } => $"looked up ({owner.Label})",
        { Existing: true } => "existing",
        { } node when inventory.Matches[node.Id].Unowned is not null => "unowned (adopt)",
        { } when target.Live is null => "missing (apply creates it)",
        { } when target.Live?.Mark is { Keeps: true } mark => $"owned, {mark.KeepReason}",
        _ => "owned",
    };
}
