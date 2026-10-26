using RepoDocsCli.Features.Docs;
using Spectre.Console;

namespace RepoDocsCli.Cli;

public static class PlanRenderer
{
    public static void Write(IAnsiConsole console, string feature, FeatureEntry entry, PlanProgress progress)
    {
        var heading = $"[bold]{Markup.Escape(EntryId.Address(feature, entry.Id))}[/] {Markup.Escape(entry.Summary)} {Count(progress.Done, progress.Total)}";
        if (progress.Cancelled > 0)
            heading += $" [grey]({progress.Cancelled} cancelled)[/]";
        if (progress.Missing.Count > 0)
            heading += $" [red]missing {string.Join(", ", progress.Missing.Select(Number))}[/]";

        var tree = new Tree(new Markup(heading));
        if (progress.Steps.Count == 0)
            tree.AddNode(new Markup("[grey]No steps.[/]"));

        foreach (var step in progress.Steps)
        {
            var node = tree.AddNode(new Markup($"{StateMarkup(step.State)} {Markup.Escape(step.Title)} {Count(step.Done, step.Total)}"));
            if (step.Issues.Count == 0)
                node.AddNode(new Markup("[grey]No linked issues.[/]"));
            foreach (var issue in step.Issues)
                node.AddNode(new Markup(IssueMarkup(issue)));
        }

        console.Write(tree);
    }

    static string Count(int done, int total) => $"[grey]{done}/{total}[/]";

    static string Number(int number) => $"{number:D4}";

    static string IssueMarkup(LinkedIssue issue)
    {
        if (issue.State == LinkedIssueState.Missing)
            return $"[red]{Number(issue.Number)} (missing)[/]";

        var text = $"{Number(issue.Number)} {Markup.Escape(issue.Name ?? "")} {IssueStateMarkup(issue.State)}";
        return issue.Blocked ? $"{text} [red]blocked by {string.Join(", ", issue.BlockedBy.Select(Number))}[/]" : text;
    }

    static string StateMarkup(StepState state) => state switch
    {
        StepState.Done => "[green]done[/]",
        StepState.InProgress => "[yellow]in-progress[/]",
        StepState.Blocked => "[red]blocked[/]",
        _ => "[blue]open[/]",
    };

    static string IssueStateMarkup(LinkedIssueState state) => state switch
    {
        LinkedIssueState.Done => "[green]Done[/]",
        LinkedIssueState.InProgress => "[yellow]InProgress[/]",
        LinkedIssueState.Cancelled => "[grey]Cancelled[/]",
        _ => "[blue]Open[/]",
    };
}
