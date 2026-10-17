using RepoIssuesCli.Features.Issues;
using Spectre.Console;

namespace RepoIssuesCli.Cli;

public sealed class Interaction(IAnsiConsole console, GlobalSettings settings)
{
    public bool IsInteractive =>
        !settings.NoInteractive
        && !settings.Json
        && Environment.GetEnvironmentVariable("RI_NONINTERACTIVE") is not ("1" or "true")
        && console.Profile.Capabilities.Interactive;

    public string RequireText(string? value, string option, string question)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        EnsureInteractive(option);
        return console.Prompt(new TextPrompt<string>(question)).Trim();
    }

    public string? AskOptionalText(string question, string? current = null)
    {
        var prompt = new TextPrompt<string>(question).AllowEmpty();
        if (!string.IsNullOrEmpty(current))
            prompt.DefaultValue(current);

        var answer = console.Prompt(prompt).Trim();
        return answer.Length == 0 ? null : answer;
    }

    public T Choose<T>(string title, IEnumerable<T> choices, Func<T, string> display, string option) where T : notnull
    {
        EnsureInteractive(option);
        return console.Prompt(new SelectionPrompt<T>()
            .Title(title)
            .PageSize(15)
            .EnableSearch()
            .UseConverter(choice => Markup.Escape(display(choice)))
            .AddChoices(choices));
    }

    public T ChooseEnum<T>(string title, T current) where T : struct, Enum =>
        console.Prompt(new SelectionPrompt<T>()
            .Title(title)
            .AddChoices(Enum.GetValues<T>())
            .DefaultValue(current));

    public IReadOnlyList<T> ChooseMany<T>(string title, IReadOnlyCollection<T> choices, IEnumerable<T> selected, Func<T, string> display)
        where T : notnull
    {
        if (choices.Count == 0)
            return [];

        var prompt = new MultiSelectionPrompt<T>()
            .Title(title)
            .PageSize(15)
            .NotRequired()
            .InstructionsText("[grey](space to toggle, enter to accept)[/]")
            .UseConverter(choice => Markup.Escape(display(choice)))
            .AddChoices(choices);
        foreach (var choice in selected)
            prompt.Select(choice);

        return console.Prompt(prompt);
    }

    public bool Confirm(string question) => console.Confirm(question, defaultValue: false);

    public Issue ChooseIssue(IReadOnlyCollection<Issue> issues, string? reference, string title)
    {
        if (!string.IsNullOrWhiteSpace(reference))
            return IssueResolver.Resolve(issues, reference);

        EnsureInteractive("<ref>");
        if (issues.Count == 0)
            throw new UsageException("There are no issues yet.");

        var ordered = issues.OrderBy(issue => issue.Status.IsClosed()).ThenBy(issue => issue.Number);
        return Choose(title, ordered, issue => $"{issue.Title} [{issue.Status}]", "<ref>");
    }

    void EnsureInteractive(string option)
    {
        if (!IsInteractive)
            throw new UsageException($"Missing required input: {option}.");
    }
}
