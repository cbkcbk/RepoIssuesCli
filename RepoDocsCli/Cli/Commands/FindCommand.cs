using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Search features and entries. Ranks an exact id above summary hits, then keyword hits, then body hits (case-insensitive). Does not record reads.")]
public sealed class FindCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<FindCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<query>")]
        [Description("Words to search for; quote several words.")]
        public string Query { get; init; } = "";

        [CommandOption("-n|--limit <N>")]
        [Description("Maximum number of results. Defaults to 10.")]
        public int Limit { get; init; } = 10;

        public override ValidationResult Validate() =>
            Limit < 1 ? ValidationResult.Error("--limit must be at least 1.")
            : string.IsNullOrWhiteSpace(Query) ? ValidationResult.Error("The query is empty.")
            : ValidationResult.Success();
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        RequireInitialized(store);
        var hits = DocsSearch.Find(LoadReportingErrors(store), settings.Query, settings.Limit).ToList();

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(hits));
        else
            DocsRenderer.WriteHits(Console, hits);
        return ExitCodes.Success;
    }
}
