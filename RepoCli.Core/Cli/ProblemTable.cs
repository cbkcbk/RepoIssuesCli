using RepoCli.Core.Features.Validation;
using Spectre.Console;

namespace RepoCli.Core.Cli;

public sealed record ProblemRow(string Kind, Severity Severity, string Message, string? File, bool Fixable = false);

public static class ProblemTable
{
    public static void Write(IAnsiConsole console, IReadOnlyList<ProblemRow> problems)
    {
        if (problems.Count == 0)
        {
            console.MarkupLine("[green]No problems found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Problem").AddColumn("Details").AddColumn("File");
        foreach (var problem in problems)
            table.AddRow(new Markup(KindMarkup(problem)), new Text(problem.Message), new Text(problem.File ?? ""));
        console.Write(table);

        var errors = problems.Count(problem => problem.Severity == Severity.Error);
        var warnings = problems.Count - errors;
        if (errors > 0)
            console.MarkupLine($"[red]{errors} error(s) found.[/]");
        if (warnings > 0)
            console.MarkupLine($"[yellow]{warnings} warning(s) found.[/]");
    }

    static string KindMarkup(ProblemRow problem) =>
        $"[{SeverityColor(problem.Severity)}]{Markup.Escape(problem.Kind)}[/]" + (problem.Fixable ? " [grey](fixable)[/]" : "");

    static string SeverityColor(Severity severity) => severity == Severity.Warning ? "yellow" : "red";
}
