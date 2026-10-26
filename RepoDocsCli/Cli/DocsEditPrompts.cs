using RepoDocsCli.Features.Docs;
using Spectre.Console;

namespace RepoDocsCli.Cli;

public sealed class DocsEditPrompts(IAnsiConsole console, Interaction interaction, DocsInput input)
{
    public Interaction Interaction => interaction;

    public DocsInput Input => input;

    public WhenHints Paths(WhenHints hints) =>
        hints with { Paths = KeepAndAdd("Keep paths:", hints.Paths, path => path, "Add path [grey](empty to finish)[/]:", input.ParsePath).Distinct().ToList() };

    public WhenHints Anchors(WhenHints hints) =>
        hints with
        {
            Anchors = KeepAndAdd("Keep anchors:", hints.Anchors, anchor => $"{anchor.Path}:{anchor.Line} {anchor.Symbol}".TrimEnd(),
                "Add anchor [grey](path:line[-end], empty to finish)[/]:", input.ParseAnchor).Distinct().ToList(),
        };

    public WhenHints Keywords(WhenHints hints)
    {
        var text = interaction.AskOptionalText("Keywords [grey](comma-separated)[/]:", string.Join(", ", hints.Keywords));
        return hints with { Keywords = DocsInput.ParseKeywords([text ?? ""]).ToList() };
    }

    public List<PlanStep> Steps(List<PlanStep> steps) =>
        KeepAndAdd("Keep steps:", steps, DocsInput.FormatStep, "Add step [grey](title #issue ..., empty to finish)[/]:", DocsInput.ParseStep);

    List<T> KeepAndAdd<T>(string keepTitle, List<T> current, Func<T, string> display, string addQuestion, Func<string, T> parse) where T : notnull
    {
        var kept = interaction.ChooseMany(keepTitle, current, current, display).ToList();
        while (interaction.AskOptionalText(addQuestion) is { } text)
        {
            try
            {
                kept.Add(parse(text));
            }
            catch (UsageException exception)
            {
                console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            }
        }

        return kept;
    }
}
