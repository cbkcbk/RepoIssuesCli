using System.Text.RegularExpressions;

namespace RepoDocsCli.Features.Import;

public static partial class KindGuess
{
    public const string Rule = "rule";
    public const string Fact = "fact";
    public const string Gotcha = "gotcha";
    public const string Howto = "howto";
    public const string Decision = "decision";

    public static string Guess(ImportCandidate candidate)
    {
        var text = string.Join("\n", [.. candidate.Headings, candidate.Summary, candidate.Body ?? ""]).Replace('’', '\'');

        if (GotchaPattern().IsMatch(text))
            return Gotcha;
        if (HowtoPattern().IsMatch(text) || NumberedStepPattern().Count(text) >= 2 || CommandPattern().IsMatch(text))
            return Howto;
        if (DecisionPattern().IsMatch(text))
            return Decision;
        if (RulePattern().IsMatch(text))
            return Rule;
        return ReasonPattern().IsMatch(text) ? Decision : Fact;
    }

    public static bool IsHowtoHeading(string? heading) => heading is not null && HowtoPattern().IsMatch(heading);

    [GeneratedRegex(@"\b(gotchas?|careful|beware|note that|watch out|pitfalls?|caveats?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GotchaPattern();

    [GeneratedRegex(@"\b(how to|steps)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HowtoPattern();

    [GeneratedRegex(@"^\s*\d+[.)]\s", RegexOptions.Multiline)]
    private static partial Regex NumberedStepPattern();

    [GeneratedRegex(@"^\s*(```|~~~)\s*(sh|bash|shell|zsh|console|powershell|pwsh|ps1|cmd|bat)\b|^\s*\$ \S", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"\b(we chose|we picked|we went with|decided|decision)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecisionPattern();

    [GeneratedRegex(@"\b(always|never|must|don't|do not|avoid)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"\bbecause\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonPattern();
}
