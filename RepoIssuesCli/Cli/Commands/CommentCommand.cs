using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Add a comment to an issue. Prompts for the text when run interactively without it.")]
public sealed class CommentCommand(IAnsiConsole console) : IssueCommand<CommentCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id.")]
        public string? Reference { get; init; }

        [CommandArgument(1, "[text]")]
        [Description("Comment text.")]
        public string? Text { get; init; }

        [CommandOption("-a|--author <AUTHOR>")]
        [Description("Author. Defaults to the git author (user.name <user.email>).")]
        public string? Author { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var interaction = session.Interaction;
        var issue = interaction.ChooseIssue(session.Issues, settings.Reference, "Comment on which issue?");
        var text = interaction.RequireMultilineText(settings.Text, "[text]", "Comment:");

        var now = Issue.Timestamp();
        var comment = new IssueComment(
            string.IsNullOrWhiteSpace(settings.Author) ? GitIdentity.Author(session.Store.Root) : settings.Author.Trim(),
            now,
            text);
        var saved = issue with { Comments = [.. issue.Comments, comment], Updated = now };
        session.Store.Save(saved);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, saved);
        else
            IssueRenderer.WriteSaved(Console, "Commented on", saved);

        return ExitCodes.Success;
    }
}
