using Spectre.Console;

namespace RepoCli.Core.Cli;

public sealed class Interaction(IAnsiConsole console, GlobalSettings settings)
{
    static readonly string[] NonInteractiveVariables = ["ISSUES_NONINTERACTIVE", "DOCS_NONINTERACTIVE"];

    public bool IsInteractive =>
        !settings.NoInteractive
        && !settings.Json
        && !NonInteractiveVariables.Any(name => Environment.GetEnvironmentVariable(name) is "1" or "true")
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

    public string RequireMultilineText(string? value, string option, string question)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        EnsureInteractive(option);
        while (true)
        {
            if (AskMultilineText(question) is { } text)
                return text;
        }
    }

    public string? AskMultilineText(string question, string? current = null)
    {
        var hint = current is null ? "blank line finishes" : "blank line finishes; blank first line keeps current, - clears";
        console.MarkupLine($"{question} [grey]({hint})[/]");

        var first = ReadLine();
        if (first.Length == 0)
            return current;
        if (first.Trim() == "-")
            return null;

        var lines = new List<string> { first };
        while (ReadLine() is { Length: > 0 } line)
            lines.Add(line);

        return string.Join('\n', lines).Trim();
    }

    string ReadLine() => console.Prompt(new TextPrompt<string>("[grey]>[/]").AllowEmpty()).TrimEnd();

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

    public void EnsureInteractive(string option)
    {
        if (!IsInteractive)
            throw new UsageException($"Missing required input: {option}.");
    }
}
