using System.ComponentModel;
using RepoDocsCli.Features.Check;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Check the docs: schema, declared kinds, merge conflicts, dead paths, anchors, plan status against linked issues and the generated CLAUDE.md block. Exits with 1 on errors.")]
public sealed class CheckCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<CheckCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--strict")]
        [Description("Exit with 1 on warnings too.")]
        public bool Strict { get; init; }

        [CommandOption("--fix")]
        [Description("Repair mechanical drift first: merge conflicts per entry, stamp hand edits, rewrite moved anchors and re-render a stale CLAUDE.md block. Then report what is left.")]
        public bool Fix { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        if (!store.IsInitialized)
            throw new UsageException($"No {RepoLocator.DocsFolderName} folder in {store.Root}; run 'docs init' first.");

        var report = settings.Fix ? DocsChecker.Fix(store) : DocsChecker.Check(store);

        if (settings.Json)
            CliOutput.WriteJson(Console, settings.Fix ? DocsJson.SerializeOutput(report) : DocsJson.SerializeOutput(report.Problems.ToList()));
        else
            Write(report, settings.Fix);

        var failed = report.Problems.Any(problem => settings.Strict || problem.Severity == Severity.Error);
        return failed ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }

    void Write(CheckReport report, bool fix)
    {
        foreach (var applied in report.Fixed)
            CliOutput.WriteUnwrappedLine(Console, $"[green]Fixed[/] {applied.Kind} {Markup.Escape(Where(applied))}: {Markup.Escape(applied.Message)}");

        if (fix && report.Problems.Count > 0)
            Console.MarkupLine("Still needs a human or agent:");

        ProblemTable.Write(Console, [.. report.Problems.Select(problem => new ProblemRow(problem.Kind.ToString(), problem.Severity, problem.Message, problem.File, problem.Fixable))]);
    }

    static string Where(ValidationProblem problem) => problem.Address ?? problem.File ?? "";
}
