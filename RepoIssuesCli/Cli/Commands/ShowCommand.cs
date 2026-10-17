using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Show an issue with its blockers and locations.")]
public sealed class ShowCommand(IAnsiConsole console) : IssueCommand<ShowCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id.")]
        public string? Reference { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var issue = session.Interaction.ChooseIssue(session.Issues, settings.Reference, "Show which issue?");

        if (settings.Json)
            IssueRenderer.WriteJson(Console, issue);
        else
            IssueRenderer.WriteDetails(Console, issue, session.Issues);

        return ExitCodes.Success;
    }
}
