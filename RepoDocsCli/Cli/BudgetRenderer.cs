using RepoDocsCli.Cli.Commands;
using RepoDocsCli.Features.Budget;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RepoDocsCli.Cli;

public static class BudgetRenderer
{
    public static void Write(IAnsiConsole console, BudgetReport report, BudgetGrouping? by)
    {
        CliOutput.WriteUnwrappedLine(console, $"[grey]Token estimate:[/] {Markup.Escape(report.Method)}");
        WriteAlwaysLoaded(console, report.AlwaysLoaded);
        WriteContextual(console, report.Contextual);

        var onDemand = report.OnDemand;
        Heading(console, $"On demand: {onDemand.Total} tokens in {onDemand.Entries} entries (summary + body, read with docs show)");
        if (by != BudgetGrouping.Kind)
            WriteByFeature(console, onDemand);
        if (by != BudgetGrouping.Feature)
            WriteByKind(console, onDemand);

        WritePush(console, report.Push);
        WriteLargest(console, report.Largest);

        foreach (var note in report.Notes)
            CliOutput.WriteUnwrappedLine(console, $"[yellow]Note:[/] {Markup.Escape(note)}");
    }

    static void WriteAlwaysLoaded(IAnsiConsole console, AlwaysLoaded loaded)
    {
        Heading(console, $"Always loaded: {loaded.Total} tokens");
        var claudeMd = loaded.ClaudeMd;
        var table = NewTable("Source", "Depth", "Tokens");
        table.AddRow(Cells($"{claudeMd.Path} (outside the block)", "0", claudeMd.OutsideBlock));
        table.AddRow(Cells($"{claudeMd.Path} (generated block)", "0", claudeMd.Block));
        foreach (var file in loaded.Imports)
            table.AddRow(Cells(new string(' ', file.Depth * 2) + file.Path, file.Depth.ToString(), file.Tokens));
        Total(table, loaded.Total);
        console.Write(table);

        if (claudeMd.BlockIfRendered is { } rendered)
        {
            var delta = rendered - claudeMd.Block;
            var state = claudeMd.Stale == true ? $"[yellow]stale, {delta:+0;-0;0}[/]; run [bold]docs render[/]" : "[green]up to date[/]";
            CliOutput.WriteUnwrappedLine(console, $"Block if rendered now: {rendered} tokens ({state})");
        }

        foreach (var external in loaded.External)
            CliOutput.WriteUnwrappedLine(console, $"[grey]External import (not counted):[/] {Markup.Escape(external.Path)} [grey]in {Markup.Escape(external.From)}[/]");
        foreach (var skipped in loaded.Skipped)
            CliOutput.WriteUnwrappedLine(console, $"[yellow]Skipped import ({ReasonText(skipped.Reason)}):[/] {Markup.Escape(skipped.Path)} [grey]in {Markup.Escape(skipped.From)}[/]");
    }

    static void WriteContextual(IAnsiConsole console, IReadOnlyList<ContextualFile> files)
    {
        if (files.Count == 0)
            return;

        Heading(console, "Contextual: nested CLAUDE.md files, loaded when working in their folder");
        var table = NewTable("Path", "Tokens", "Imports");
        foreach (var file in files)
            table.AddRow(new Text(file.Path), Number(file.Tokens), Number(file.ImportTokens));
        console.Write(table);
    }

    static void WriteByFeature(IAnsiConsole console, OnDemand onDemand)
    {
        var table = NewTable("Feature", "Entries", "Summary", "Body", "Tokens");
        foreach (var cost in onDemand.ByFeature)
            table.AddRow(new Text(cost.Feature), Number(cost.Entries), Number(cost.SummaryTokens), Number(cost.BodyTokens), Number(cost.Tokens));
        Total(table, onDemand.Total);
        console.Write(table);
    }

    static void WriteByKind(IAnsiConsole console, OnDemand onDemand)
    {
        var table = NewTable(["Kind", "Render", "Push"], "Entries", "Summary", "Body", "Tokens");
        foreach (var cost in onDemand.ByKind)
            table.AddRow(
                new Text(cost.Kind),
                new Text(cost.Render is { } render ? Name(render) : "undeclared"),
                new Text(cost.Push is { } push ? Name(push) : "-"),
                Number(cost.Entries),
                Number(cost.SummaryTokens),
                Number(cost.BodyTokens),
                Number(cost.Tokens));
        Total(table, onDemand.Total);
        console.Write(table);
    }

    static void WritePush(IAnsiConsole console, PushBudget push)
    {
        Heading(console, $"Push: up to {push.Total} tokens injected by hooks (bodies over {DocsFor.ShortBodyLength} chars as summary only)");
        var table = NewTable("Policy", "Entries", "Full", "Summary only", "Tokens");
        foreach (var cost in push.ByPolicy)
            table.AddRow(new Text(Name(cost.Policy)), Number(cost.Entries), Number(cost.Full), Number(cost.SummaryOnly), Number(cost.Tokens));
        Total(table, push.Total);
        console.Write(table);
    }

    static void WriteLargest(IAnsiConsole console, IReadOnlyList<EntryCost> largest)
    {
        if (largest.Count == 0)
            return;

        Heading(console, "Largest entries: condensing targets");
        var table = NewTable(["Address", "Kind"], "Tokens");
        foreach (var entry in largest)
            table.AddRow(new Markup($"[bold]{Markup.Escape(entry.Address)}[/]"), new Text(entry.Kind), Number(entry.Tokens));
        console.Write(table);
    }

    static void Heading(IAnsiConsole console, string text) => CliOutput.WriteUnwrappedLine(console, $"\n[bold]{Markup.Escape(text)}[/]");

    static Table NewTable(string first, params string[] numeric) => NewTable([first], numeric);

    static Table NewTable(string[] labels, params string[] numeric)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in labels)
            table.AddColumn(new TableColumn(column));
        foreach (var column in numeric)
            table.AddColumn(new TableColumn(column).RightAligned().NoWrap());
        return table;
    }

    static void Total(Table table, int total)
    {
        var cells = Enumerable.Range(0, table.Columns.Count).Select(index =>
            index == 0 ? new Markup("[bold]Total[/]")
            : index == table.Columns.Count - 1 ? new Markup($"[bold]{total}[/]")
            : (IRenderable) Text.Empty);
        table.AddRow(cells);
    }

    static IRenderable[] Cells(string source, string depth, int tokens) => [new Text(source), new Text(depth), Number(tokens)];

    static Text Number(int value) => new(value.ToString());

    static string Name(RenderMode mode) => mode.ToString().ToLowerInvariant();

    static string Name(PushPolicy policy) => policy switch
    {
        PushPolicy.OnEdit => "on-edit",
        PushPolicy.OnRead => "on-read",
        PushPolicy.WhenFeatureActive => "when-feature-active",
        _ => "never",
    };

    static string ReasonText(SkipReason reason) => reason == SkipReason.TooDeep ? $"deeper than {ClaudeMdImports.MaxDepth} hops" : "missing";
}
