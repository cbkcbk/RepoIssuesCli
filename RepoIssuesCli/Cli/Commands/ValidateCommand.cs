using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Check for invalid files, duplicate numbers, missing blockers and blocker cycles, and warn about locations that moved, changed or can't be found. Exits with 1 on errors.")]
public sealed class ValidateCommand(IAnsiConsole console) : IssueCommand<ValidateCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--strict")]
        [Description("Exit with 1 on warnings too.")]
        public bool Strict { get; init; }

        [CommandOption("--fix")]
        [Description("Rewrite the line hints of locations whose code moved unchanged. Changed, missing and uncertain matches are only reported. Does not bump updated.")]
        public bool Fix { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var store = session.Store;
        var locations = new IssueLocations(store.Root);
        var snapshot = session.Snapshot;
        IReadOnlyList<LocationFix> fixes = [];
        if (settings.Fix)
            (snapshot, fixes) = FixMoved(store, locations);

        var problems = IssueValidator.Validate(snapshot, store.LineCount, locations.Resolve);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, fixes.Select(ValidationProblem.From).Concat(problems).ToList());
        else
            IssueRenderer.WriteProblems(Console, problems, fixes);

        var failed = problems.Any(problem => settings.Strict || problem.Severity == Severity.Error);
        return failed ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }

    static (IssueSnapshot Snapshot, IReadOnlyList<LocationFix> Fixes) FixMoved(IssueStore store, IssueLocations locations)
    {
        using var lease = store.Lock();
        var snapshot = store.Load();
        var fixes = new List<LocationFix>();
        var issues = snapshot.Issues.Select(issue =>
        {
            var (rehinted, applied) = locations.FixMoved(issue);
            if (applied.Count == 0)
                return issue;

            fixes.AddRange(applied);
            return store.Save(rehinted);
        }).ToList();

        return (snapshot with { Issues = issues }, fixes);
    }
}
