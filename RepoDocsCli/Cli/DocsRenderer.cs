using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RepoDocsCli.Cli;

public static class DocsRenderer
{
    public const string NoIssuesNote = "progress unavailable: no .repo-issues folder";

    public static void WriteFeature(IAnsiConsole console, FeatureView feature)
    {
        var grid = NewGrid();
        if (feature.Status is { } status)
            grid.AddRow(new Markup("[grey]Status[/]"), new Markup(StatusMarkup(status)));
        if (feature.Summary is { } summary)
            grid.AddRow(new Markup("[grey]Summary[/]"), new Text(summary));
        AddHints(grid, feature.When);
        AddTimestamp(grid, "Created", feature.Created);
        AddTimestamp(grid, "Updated", feature.Updated);

        var entries = new Tree("[bold]Entries[/]");
        if (feature.Entries.Count == 0)
            entries.AddNode(new Markup("[grey]No entries.[/]"));
        foreach (var entry in feature.Entries)
            AddEntry(entries.AddNode, entry);

        WritePanel(console, feature.Feature, [grid, entries]);
    }

    public static void WriteEntry(IAnsiConsole console, EntryView entry)
    {
        var grid = NewGrid();
        grid.AddRow(new Markup("[grey]Kind[/]"), new Text(entry.Kind));
        grid.AddRow(new Markup("[grey]Summary[/]"), new Text(entry.Summary));
        AddHints(grid, entry.When);
        AddTimestamp(grid, "Created", entry.Created);
        AddTimestamp(grid, "Updated", entry.Updated);
        if (entry.Read is { } read)
            grid.AddRow(new Markup("[grey]Read[/]"), new Text(read.ToString(DocsTime.DateFormat)));

        var sections = new List<IRenderable> { grid };
        if (entry.Body is { } body)
            sections.Add(new Panel(new Text(body.TrimEnd())).Header("Body").Border(BoxBorder.Rounded).Expand());
        if (entry.Kind == PlanStep.EntryKind)
            sections.Add(PlanTree(entry));

        WritePanel(console, entry.Address, sections);
    }

    public static void WriteFor(IAnsiConsole console, ForResult result)
    {
        var path = result.Path.Length == 0 ? "." : result.Path;
        if (result.Entries.Count == 0)
        {
            CliOutput.WriteUnwrappedLine(console, $"[grey]No docs apply to {Markup.Escape(path)}.[/]");
            return;
        }

        var tree = new Tree($"[bold]{Markup.Escape(path)}[/]");
        foreach (var group in result.Entries.GroupBy(entry => entry.Feature))
        {
            var owner = result.Features.FirstOrDefault(feature => feature.Feature == group.Key);
            var node = tree.AddNode(new Markup(owner is null ? $"[bold]{Markup.Escape(group.Key)}[/]" : FeatureLabel(owner)));
            foreach (var entry in group)
                AddEntry(node.AddNode, entry);
        }

        console.Write(tree);
    }

    public static void WriteHits(IAnsiConsole console, IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
        {
            console.MarkupLine("[grey]No matches.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Score").RightAligned().NoWrap())
            .AddColumn(new TableColumn("Address").NoWrap())
            .AddColumn(new TableColumn("Kind").NoWrap())
            .AddColumn(new TableColumn("Summary"));
        foreach (var hit in hits)
            table.AddRow(new Text(hit.Score.ToString()), new Markup($"[bold]{Markup.Escape(hit.Address)}[/]"), new Text(hit.Kind), new Text(hit.Summary ?? ""));
        console.Write(table);
    }

    public static void WriteList(IAnsiConsole console, IReadOnlyList<FeatureListItem> features)
    {
        if (features.Count == 0)
        {
            console.MarkupLine("[grey]No features.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Feature").NoWrap())
            .AddColumn(new TableColumn("Status").NoWrap())
            .AddColumn(new TableColumn("Entries").RightAligned().NoWrap())
            .AddColumn(new TableColumn("Summary"));
        foreach (var feature in features)
            table.AddRow(
                new Markup($"[bold]{Markup.Escape(feature.Feature)}[/]"),
                new Markup(StatusMarkup(feature.Status)),
                new Text(feature.EntryCount.ToString()),
                new Text(feature.Summary ?? ""));
        console.Write(table);
    }

    static void AddEntry(Func<IRenderable, TreeNode> add, EntryView entry)
    {
        var more = entry is { HasBody: true, Body: null } ? " [grey](+ body)[/]" : "";
        var node = entry.Progress is { } progress
            ? add(PlanRenderer.BuildTree(entry.Address, entry.Summary, progress))
            : add(new Markup($"[bold]{Markup.Escape(entry.Address)}[/] [grey]{Markup.Escape(entry.Kind)}[/] {Markup.Escape(entry.Summary)}{more}"));
        if (entry.Body is { } body)
            node.AddNode(new Text(body.TrimEnd()));
        if (entry is { Kind: PlanStep.EntryKind, Progress: null })
            node.AddNode(PlanTree(entry));
    }

    static IRenderable PlanTree(EntryView entry)
    {
        if (entry.Progress is { } progress)
            return PlanRenderer.BuildTree(entry.Address, entry.Summary, progress);

        var tree = new Tree($"[bold]Steps[/] [grey]({NoIssuesNote})[/]");
        if (entry.Steps.Count == 0)
            tree.AddNode(new Markup("[grey]No steps.[/]"));
        foreach (var step in entry.Steps)
        {
            var issues = step.Issues.Count == 0 ? "" : $" [grey]{string.Join(", ", step.Issues.Select(number => $"{number:D4}"))}[/]";
            tree.AddNode(new Markup(Markup.Escape(step.Title) + issues));
        }

        return tree;
    }

    static Grid NewGrid() => new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();

    static void AddHints(Grid grid, WhenHints? when)
    {
        if (when is null)
            return;
        if (when.Paths.Count > 0)
            grid.AddRow(new Markup("[grey]Paths[/]"), new Text(string.Join(", ", when.Paths)));
        if (when.Anchors.Count > 0)
            grid.AddRow(new Markup("[grey]Anchors[/]"), new Text(string.Join(", ", when.Anchors.Select(anchor => $"{anchor.Path}:{anchor.Line}{(anchor.Symbol is { } symbol ? $" {symbol}" : "")}"))));
        if (when.Keywords.Count > 0)
            grid.AddRow(new Markup("[grey]Keywords[/]"), new Text(string.Join(", ", when.Keywords)));
    }

    static void AddTimestamp(Grid grid, string label, DateTimeOffset? value)
    {
        if (value is { } timestamp)
            grid.AddRow(new Markup($"[grey]{label}[/]"), new Text(timestamp.ToString(DocsTime.TimestampFormat)));
    }

    static void WritePanel(IAnsiConsole console, string title, IReadOnlyList<IRenderable> sections) =>
        console.Write(new Panel(new Rows(sections))
            .Header($" {Markup.Escape(title)} ")
            .Border(BoxBorder.Rounded)
            .Expand());

    static string FeatureLabel(FeatureRef feature)
    {
        var label = $"[bold]{Markup.Escape(feature.Feature)}[/] {StatusMarkup(feature.Status)}";
        return feature.Summary is { } summary ? $"{label} {Markup.Escape(summary)}" : label;
    }

    static string StatusMarkup(FeatureStatus status) => status switch
    {
        FeatureStatus.Planned => $"[blue]{status.Name()}[/]",
        FeatureStatus.InProgress => $"[yellow]{status.Name()}[/]",
        FeatureStatus.Shipped => $"[green]{status.Name()}[/]",
        _ => $"[grey]{status.Name()}[/]",
    };
}
