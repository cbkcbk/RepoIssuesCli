using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Give an issue the next free number, e.g. to fix a duplicate number after a merge. Blockers on an unshared number follow the issue; blockers on a duplicated number keep pointing at the issue that keeps it.")]
public sealed class RenumberCommand(IAnsiConsole console) : IssueCommand<RenumberCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id. Use the id or name to pick one of several issues sharing a number.")]
        public string? Reference { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var issue = session.Interaction.ChooseIssue(session.Issues, settings.Reference, "Renumber which issue?");
        var result = IssueRenumbering.Renumber(session.Snapshot, issue);

        session.Store.Save(result.Issue);
        foreach (var rewritten in result.Rewritten)
            session.Store.Save(rewritten);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, result);
        else
            WriteSummary(session.Issues, issue, result);

        return ExitCodes.Success;
    }

    void WriteSummary(IReadOnlyList<Issue> issues, Issue original, RenumberResult result)
    {
        Console.MarkupLine($"Renumbered [bold]{Markup.Escape(original.Title)}[/] to [bold]{Markup.Escape(result.Issue.Title)}[/]");

        if (result.Rewritten.Count > 0)
            Console.MarkupLine($"[grey]Updated blockers of:[/] {Titles(result.Rewritten)}");

        if (result.KeptReferences.Count == 0)
            return;

        var holders = issues.Where(other => other.Id != original.Id && other.Number == result.PreviousNumber).ToList();
        Console.MarkupLine($"[yellow]Blockers on {result.PreviousNumber:D4} still mean {Titles(holders)}:[/] {Titles(result.KeptReferences)}");
        Console.MarkupLine($"[grey]If one meant {Markup.Escape(result.Issue.Title)}, run 'issues edit <ref> --remove-blocker {result.PreviousNumber} --add-blocker {result.Issue.Number}'.[/]");
    }

    static string Titles(IEnumerable<Issue> issues) => Markup.Escape(string.Join(", ", issues.Select(issue => issue.Title)));
}
