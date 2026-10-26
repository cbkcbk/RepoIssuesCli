using System.ComponentModel;
using RepoCli.Core.Features.IssueLinks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Show an issue with its blockers, locations and the .repo-docs plan steps that link it.")]
public sealed class ShowCommand(IAnsiConsole console, ErrorConsole errors) : IssueCommand<ShowCommand.Settings>(console)
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
        var docs = errors.Report(new DocsSteps(session.Store.Root).Find(issue.Number)).References;

        if (settings.Json)
            IssueRenderer.WriteJson(Console, issue, docs);
        else
            IssueRenderer.WriteDetails(Console, issue, session.Issues, docs);

        return ExitCodes.Success;
    }
}
