using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;

namespace RepoIssuesCli.Cli.Commands;

[Description("Check for invalid files, duplicate numbers, missing blockers and blocker cycles. Exits with 1 on problems.")]
public sealed class ValidateCommand(IAnsiConsole console) : IssueCommand<GlobalSettings>(console)
{
    protected override int Run(IssueSession session, GlobalSettings settings)
    {
        var problems = IssueValidator.Validate(session.Snapshot);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, problems.ToList());
        else
            IssueRenderer.WriteProblems(Console, problems);

        return problems.Count == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }
}
