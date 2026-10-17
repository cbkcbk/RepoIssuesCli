using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RepoIssuesCli.Cli;

public static class IssueRenderer
{
    public static void WriteJson<T>(IAnsiConsole console, T value) => console.Profile.Out.Writer.Write(IssueJson.Serialize(value));

    public static void WriteList(IAnsiConsole console, IReadOnlyList<Issue> shown, IReadOnlyCollection<Issue> all)
    {
        if (shown.Count == 0)
        {
            console.MarkupLine("[grey]No issues.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("Name")
            .AddColumn("Status")
            .AddColumn("Priority")
            .AddColumn("Tags")
            .AddColumn("Blocked by");

        foreach (var issue in shown)
        {
            table.AddRow(
                new Markup($"[bold]{issue.Number:D4}[/]"),
                new Text(issue.Name),
                Status(issue.Status),
                Priority(issue.Priority),
                new Text(string.Join(", ", issue.Tags)),
                BlockerSummary(issue, all));
        }

        console.Write(table);
    }

    public static void WriteDetails(IAnsiConsole console, Issue issue, IReadOnlyCollection<Issue> all)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow(new Markup("[grey]Status[/]"), Status(issue.Status));
        grid.AddRow(new Markup("[grey]Priority[/]"), Priority(issue.Priority));
        if (issue.Tags.Count > 0)
            grid.AddRow(new Markup("[grey]Tags[/]"), new Text(string.Join(", ", issue.Tags)));
        grid.AddRow(new Markup("[grey]Created[/]"), new Text(issue.Created.ToString("u")));
        grid.AddRow(new Markup("[grey]Updated[/]"), new Text(issue.Updated.ToString("u")));
        grid.AddRow(new Markup("[grey]Id[/]"), new Text(issue.Id.ToString("D")));

        var sections = new List<IRenderable> { grid };
        if (!string.IsNullOrWhiteSpace(issue.Description))
            sections.Add(new Panel(new Text(issue.Description)).Header("Description").Border(BoxBorder.Rounded).Expand());

        if (issue.Locations.Count > 0)
        {
            var locations = new Tree("[bold]Locations[/]");
            foreach (var location in issue.Locations)
                locations.AddNode(new Text(location.ToString()));
            sections.Add(locations);
        }

        if (issue.BlockedBy.Count > 0)
            sections.Add(BlockerTree(issue, all));

        var blocks = BlockerGraph.Blocks(issue, all);
        if (blocks.Count > 0)
        {
            var tree = new Tree("[bold]Blocks[/]");
            foreach (var blocked in blocks)
                tree.AddNode(IssueLabel(blocked));
            sections.Add(tree);
        }

        console.Write(new Panel(new Rows(sections))
            .Header($" {Markup.Escape(issue.Title)} ")
            .Border(BoxBorder.Rounded)
            .Expand());
    }

    public static void WriteProblems(IAnsiConsole console, IReadOnlyList<ValidationProblem> problems)
    {
        if (problems.Count == 0)
        {
            console.MarkupLine("[green]No problems found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Problem").AddColumn("Details").AddColumn("File");
        foreach (var problem in problems)
            table.AddRow(new Markup($"[red]{problem.Kind}[/]"), new Text(problem.Message), new Text(problem.File ?? ""));
        console.Write(table);
        console.MarkupLine($"[red]{problems.Count} problem(s) found.[/]");
    }

    public static void WriteSaved(IAnsiConsole console, string verb, Issue issue) =>
        console.MarkupLine($"{verb} [bold]{Markup.Escape(issue.Title)}[/]");

    static Tree BlockerTree(Issue issue, IReadOnlyCollection<Issue> all)
    {
        var tree = new Tree("[bold]Blocked by[/]");
        var visited = new HashSet<int> { issue.Number };
        AddBlockers(tree.AddNode, issue, all, visited);
        return tree;
    }

    static void AddBlockers(Func<IRenderable, TreeNode> add, Issue issue, IReadOnlyCollection<Issue> all, HashSet<int> visited)
    {
        foreach (var number in issue.BlockedBy)
        {
            var blocker = all.FirstOrDefault(candidate => candidate.Number == number);
            if (blocker is null)
            {
                add(new Markup($"[red]{number:D4} (missing)[/]"));
                continue;
            }

            var node = add(IssueLabel(blocker));
            if (visited.Add(number))
                AddBlockers(node.AddNode, blocker, all, visited);
        }
    }

    static Markup IssueLabel(Issue issue) =>
        new($"{Markup.Escape(issue.Title)} {StatusMarkup(issue.Status)}");

    static IRenderable BlockerSummary(Issue issue, IReadOnlyCollection<Issue> all)
    {
        if (issue.BlockedBy.Count == 0)
            return Text.Empty;

        var parts = issue.BlockedBy.Select(number =>
        {
            var blocker = all.FirstOrDefault(candidate => candidate.Number == number);
            return blocker switch
            {
                null => $"[red]{number:D4}?[/]",
                { Status: var status } when status.IsClosed() => $"[grey strikethrough]{number:D4}[/]",
                _ => $"[yellow]{number:D4}[/]",
            };
        });
        return new Markup(string.Join(", ", parts));
    }

    static Markup Status(IssueStatus status) => new(StatusMarkup(status));

    static string StatusMarkup(IssueStatus status) => status switch
    {
        IssueStatus.Open => "[blue]Open[/]",
        IssueStatus.InProgress => "[yellow]InProgress[/]",
        IssueStatus.Done => "[green]Done[/]",
        IssueStatus.Cancelled => "[grey]Cancelled[/]",
        _ => status.ToString(),
    };

    static Markup Priority(IssuePriority priority) => new(priority switch
    {
        IssuePriority.Critical => "[bold red]Critical[/]",
        IssuePriority.High => "[red]High[/]",
        IssuePriority.Medium => "Medium",
        IssuePriority.Low => "[grey]Low[/]",
        _ => priority.ToString(),
    });
}
