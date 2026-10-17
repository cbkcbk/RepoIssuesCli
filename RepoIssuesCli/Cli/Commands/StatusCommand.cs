using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Change an issue's status.")]
public sealed class StatusCommand(IAnsiConsole console) : IssueCommand<StatusCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id.")]
        public string? Reference { get; init; }

        [CommandArgument(1, "[status]")]
        [Description("Open, InProgress, Done or Cancelled.")]
        [TypeConverter(typeof(EnumOptionConverter<IssueStatus>))]
        public IssueStatus? Status { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var interaction = session.Interaction;
        var issue = interaction.ChooseIssue(session.Issues, settings.Reference, "Change status of which issue?");
        var status = settings.Status
            ?? interaction.Choose("Status:", Enum.GetValues<IssueStatus>(), value => value.ToString(), "[status]");

        var saved = issue;
        if (status != issue.Status)
        {
            saved = issue with { Status = status, Updated = Issue.Timestamp() };
            session.Store.Save(saved);
        }

        if (settings.Json)
            IssueRenderer.WriteJson(Console, saved);
        else
            IssueRenderer.WriteSaved(Console, $"{status}:", saved);

        return ExitCodes.Success;
    }
}
