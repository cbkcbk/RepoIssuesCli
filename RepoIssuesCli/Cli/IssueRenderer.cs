using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RepoIssuesCli.Cli;

public static class IssueRenderer
{
    const int PreferredNameWidth = 50;
    const int MinOptionalWidth = 10;
    const int ColumnChrome = 3;

    public static void WriteJson<T>(IAnsiConsole console, T value) => console.Profile.Out.Writer.Write(IssueJson.SerializeOutput(value));

    public static void WriteList(IAnsiConsole console, IReadOnlyList<Issue> shown, IReadOnlyCollection<Issue> all)
    {
        if (shown.Count == 0)
        {
            console.MarkupLine("[grey]No issues.[/]");
            return;
        }

        var numbers = shown.Select(issue => $"[bold]{issue.Number:D4}[/]").ToList();
        var tags = shown.Select(issue => issue.Tags.Select(Markup.Escape).ToList()).ToList();
        var blockers = shown.Select(issue => BlockerItems(issue, all)).ToList();

        var spare = console.Profile.Width - 1
            - ColumnWidth("#", numbers)
            - ColumnWidth("Name", shown.Select(issue => Markup.Escape(issue.Name)).ToList(), PreferredNameWidth)
            - ColumnWidth("Status", shown.Select(issue => StatusMarkup(issue.Status)).ToList())
            - ColumnWidth("Priority", shown.Select(issue => PriorityMarkup(issue.Priority)).ToList());
        var blockerWidth = Allot("Blocked by", blockers, ref spare);
        var tagWidth = Allot("Tags", tags, ref spare);

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").NoWrap())
            .AddColumn(new TableColumn("Name"))
            .AddColumn(new TableColumn("Status").NoWrap())
            .AddColumn(new TableColumn("Priority").NoWrap());
        if (tagWidth is { } tagColumnWidth)
            table.AddColumn(new TableColumn("Tags").Width(tagColumnWidth).NoWrap());
        if (blockerWidth is { } blockerColumnWidth)
            table.AddColumn(new TableColumn("Blocked by").Width(blockerColumnWidth).NoWrap());

        foreach (var (index, issue) in shown.Index())
        {
            List<IRenderable> cells = [new Markup(numbers[index]), new Text(issue.Name), Status(issue.Status), Priority(issue.Priority)];
            if (tagWidth is { } tagFit)
                cells.Add(FitItems(tags[index], tagFit));
            if (blockerWidth is { } blockerFit)
                cells.Add(FitItems(blockers[index], blockerFit));
            table.AddRow(cells);
        }

        console.Write(table);
    }

    static int ColumnWidth(string header, IReadOnlyList<string> cells, int limit = int.MaxValue) =>
        Math.Max(header.Length, Math.Min(limit, cells.Max(PlainWidth))) + ColumnChrome;

    static int? Allot(string header, IReadOnlyList<List<string>> items, ref int spare)
    {
        var natural = items.Max(list => PlainWidth(string.Join(", ", list)));
        var full = Math.Max(natural, header.Length);
        var width = Math.Min(full, spare - ColumnChrome);
        if (natural == 0 || width < Math.Min(full, Math.Max(header.Length, MinOptionalWidth)))
            return null;

        spare -= width + ColumnChrome;
        return width;
    }

    static Markup FitItems(List<string> items, int width)
    {
        for (var count = items.Count; count > 0; count--)
        {
            var joined = string.Join(", ", items.Take(count));
            var text = count == items.Count ? joined : $"{joined}, …";
            if (PlainWidth(text) <= width)
                return new Markup(text);
        }

        return new Markup(items.Count == 0 ? "" : "…");
    }

    static int PlainWidth(string markup) => Markup.Remove(markup).GetCellWidth();

    public static void WriteDetails(IAnsiConsole console, Issue issue, IReadOnlyCollection<Issue> all)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow(new Markup("[grey]Status[/]"), Status(issue.Status));
        grid.AddRow(new Markup("[grey]Priority[/]"), Priority(issue.Priority));
        if (issue.Author is not null)
            grid.AddRow(new Markup("[grey]Author[/]"), new Text(issue.Author));
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

        if (issue.Comments.Count > 0)
        {
            var comments = new Tree("[bold]Comments[/]");
            foreach (var comment in issue.Comments)
                comments.AddNode(new Rows(
                    new Markup($"[grey]{Markup.Escape(comment.Author ?? "unknown")} · {comment.Created:u}[/]"),
                    new Text(comment.Text)));
            sections.Add(comments);
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

    public static void WriteUnwrappedLine(IAnsiConsole console, string markup)
    {
        console.Write(new Unwrapped(new Markup(markup)));
        console.WriteLine();
    }

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

    static List<string> BlockerItems(Issue issue, IReadOnlyCollection<Issue> all) =>
        issue.BlockedBy.Select(number =>
        {
            var blocker = all.FirstOrDefault(candidate => candidate.Number == number);
            return blocker switch
            {
                null => $"[red]{number:D4} (missing)[/]",
                { Status: var status } when status.IsClosed() => $"[grey strikethrough]{number:D4}[/]",
                _ => $"[yellow]{number:D4}[/]",
            };
        }).ToList();

    static Markup Status(IssueStatus status) => new(StatusMarkup(status));

    static string StatusMarkup(IssueStatus status) => status switch
    {
        IssueStatus.Open => "[blue]Open[/]",
        IssueStatus.InProgress => "[yellow]InProgress[/]",
        IssueStatus.Done => "[green]Done[/]",
        IssueStatus.Cancelled => "[grey]Cancelled[/]",
        _ => status.ToString(),
    };

    static Markup Priority(IssuePriority priority) => new(PriorityMarkup(priority));

    static string PriorityMarkup(IssuePriority priority) => priority switch
    {
        IssuePriority.Critical => "[bold red]Critical[/]",
        IssuePriority.High => "[red]High[/]",
        IssuePriority.Medium => "Medium",
        IssuePriority.Low => "[grey]Low[/]",
        _ => priority.ToString(),
    };
}
