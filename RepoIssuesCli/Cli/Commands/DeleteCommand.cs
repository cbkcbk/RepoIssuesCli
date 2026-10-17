using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Delete an issue and remove it from other issues' blockers.")]
public sealed class DeleteCommand(IAnsiConsole console) : IssueCommand<DeleteCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id.")]
        public string? Reference { get; init; }

        [CommandOption("-f|--force")]
        [Description("Delete without confirmation. Required when not interactive.")]
        public bool Force { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var interaction = session.Interaction;
        var issues = session.Issues;
        var issue = interaction.ChooseIssue(issues, settings.Reference, "Delete which issue?");

        if (!settings.Force)
        {
            if (!interaction.IsInteractive)
                throw new UsageException("Deleting requires --force when not interactive.");
            if (!interaction.Confirm($"Delete {Markup.Escape(issue.Title)}?"))
            {
                Console.MarkupLine("[grey]Cancelled.[/]");
                return ExitCodes.Success;
            }
        }

        session.Store.Delete(issue);

        var numberStillUsed = issues.Any(other => other.Id != issue.Id && other.Number == issue.Number);
        if (!numberStillUsed)
        {
            foreach (var blocked in issues.Where(other => other.BlockedBy.Contains(issue.Number)))
                session.Store.Save(blocked with { BlockedBy = blocked.BlockedBy.Where(number => number != issue.Number).ToList() });
        }

        if (settings.Json)
            IssueRenderer.WriteJson(Console, issue);
        else
            IssueRenderer.WriteSaved(Console, "Deleted", issue);

        return ExitCodes.Success;
    }
}
