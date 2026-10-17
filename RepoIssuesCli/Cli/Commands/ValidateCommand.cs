using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Check for invalid files, duplicate numbers, missing blockers and blocker cycles, and warn about locations pointing at missing files or lines. Exits with 1 on errors.")]
public sealed class ValidateCommand(IAnsiConsole console) : IssueCommand<ValidateCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--strict")]
        [Description("Exit with 1 on warnings too.")]
        public bool Strict { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var problems = IssueValidator.Validate(session.Snapshot, session.Store.LineCount);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, problems.ToList());
        else
            IssueRenderer.WriteProblems(Console, problems);

        var failed = problems.Any(problem => settings.Strict || problem.Severity == Severity.Error);
        return failed ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }
}
