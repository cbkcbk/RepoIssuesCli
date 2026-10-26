using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("List features with their status, summary and entry count, sorted by id. Does not record reads.")]
public sealed class ListCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<ListCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("-s|--status <STATUS>")]
        [Description("Only these statuses (proposed, planned, in-progress, shipped, deprecated); repeatable.")]
        [TypeConverter(typeof(EnumOptionConverter<FeatureStatus>))]
        public FeatureStatus[] Statuses { get; init; } = [];
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        RequireInitialized(store);
        var features = LoadReportingErrors(store).Features
            .Where(feature => settings.Statuses.Length == 0 || settings.Statuses.Contains(feature.Status))
            .OrderBy(feature => feature.Id, StringComparer.Ordinal)
            .Select(FeatureListItem.Create)
            .ToList();

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(features));
        else
            DocsRenderer.WriteList(Console, features);
        return ExitCodes.Success;
    }
}
