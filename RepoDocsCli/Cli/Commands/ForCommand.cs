using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("List the docs that apply to a file or folder: repo.yaml entries plus entries whose when.paths or anchors match. Leaves out decision entries, and plan entries unless their feature is in-progress. Short bodies are included. Does not record reads.")]
public sealed class ForCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<ForCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("A file or folder, absolute or relative to the current directory.")]
        public string Path { get; init; } = "";

        [CommandOption("--plans")]
        [Description("Include plan entries of features that are not in-progress.")]
        public bool Plans { get; init; }

        [CommandOption("--all-kinds")]
        [Description("Include every kind, also decision and plan entries.")]
        public bool AllKinds { get; init; }

        [CommandOption("--full")]
        [Description("Include every body, not only those of up to 600 characters.")]
        public bool Full { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        RequireInitialized(store);
        var path = DocsFor.RepoRelative(store.Root, settings.Path, Environment.CurrentDirectory);
        var plans = new PlanLookup(store.Root);
        var result = DocsFor.Query(LoadReportingErrors(store), path, new ForOptions(settings.Plans, settings.AllKinds, settings.Full), plans);
        ReportPlanIssues(plans);

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(result));
        else
            DocsRenderer.WriteFor(Console, result);
        return ExitCodes.Success;
    }
}
