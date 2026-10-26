using System.Text.RegularExpressions;

namespace RepoDocsCli.Features.Import;

public sealed record ImportCandidate(IReadOnlyList<string> Headings, string Summary, string? Body, int Line);

public static partial class ImportSplitter
{
    public static IEnumerable<ImportCandidate> Split(MarkdownSection section, bool headingIsSummary)
    {
        var content = TrimBlankLines(section.Content);
        if (content.Count == 0)
            return [];

        if (!section.HasSubsections && !KindGuess.IsHowtoHeading(section.Heading) && Bullets(content) is { Count: >= 2 } bullets)
            return bullets.Select(bullet => FromParagraph(section.Headings, bullet)).OfType<ImportCandidate>().ToList();

        if (headingIsSummary && section.Heading is { Length: > 0 } heading)
            return [new ImportCandidate(section.Headings, CleanSummary(heading), Join(content), section.Line)];

        return FromParagraph(section.Headings, content) is { } candidate ? [candidate] : [];
    }

    static ImportCandidate? FromParagraph(IReadOnlyList<string> headings, IReadOnlyList<MarkdownLine> lines)
    {
        var first = ListMarkerPattern().Replace(lines[0].Text.Trim(), "");
        var (summary, rest) = FirstSentencePattern().Match(first) is { Success: true } sentence
            ? (sentence.Groups[1].Value, sentence.Groups[2].Value)
            : (first, "");

        var cleaned = CleanSummary(summary);
        if (cleaned.Length == 0)
            return null;

        List<MarkdownLine> remaining = rest.Length > 0 ? [lines[0] with { Text = rest }, .. lines.Skip(1)] : [.. lines.Skip(1)];
        return new ImportCandidate(headings, cleaned, Join(TrimBlankLines(remaining)), lines[0].Number);
    }

    static List<List<MarkdownLine>>? Bullets(IReadOnlyList<MarkdownLine> content)
    {
        var bullets = new List<List<MarkdownLine>>();
        foreach (var line in content)
        {
            if (BulletPattern().Match(line.Text) is { Success: true } bullet)
                bullets.Add([line with { Text = bullet.Groups[1].Value }]);
            else if (bullets.Count > 0 && (line.Text.Trim().Length == 0 || char.IsWhiteSpace(line.Text[0])))
                bullets[^1].Add(line);
            else
                return null;
        }

        return bullets.Select(lines => Dedented(TrimBlankLines(lines))).ToList();
    }

    static List<MarkdownLine> Dedented(List<MarkdownLine> bullet)
    {
        var continuation = bullet.Skip(1).Where(line => line.Text.Trim().Length > 0).ToList();
        if (continuation.Count == 0)
            return bullet;

        var indent = continuation.Min(line => line.Text.Length - line.Text.TrimStart().Length);
        return [bullet[0], .. bullet.Skip(1).Select(line => line with { Text = line.Text.Length >= indent ? line.Text[indent..] : "" })];
    }

    static List<MarkdownLine> TrimBlankLines(IReadOnlyList<MarkdownLine> lines)
    {
        var start = 0;
        var end = lines.Count;
        while (start < end && lines[start].Text.Trim().Length == 0)
            start++;
        while (end > start && lines[end - 1].Text.Trim().Length == 0)
            end--;
        return lines.Skip(start).Take(end - start).ToList();
    }

    static string? Join(IReadOnlyList<MarkdownLine> lines)
    {
        if (lines.Count == 0)
            return null;

        var indent = lines.Where(line => line.Text.Trim().Length > 0).Min(line => line.Text.Length - line.Text.TrimStart().Length);
        return string.Join("\n", lines.Select(line => (line.Text.Length >= indent ? line.Text[indent..] : "").TrimEnd()));
    }

    static string CleanSummary(string summary) => summary.Trim().TrimEnd(':', '.').TrimEnd();

    [GeneratedRegex(@"^ ?[-*+][ \t]+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])[ \t]+")]
    private static partial Regex ListMarkerPattern();

    [GeneratedRegex(@"^(.+?[.!?])\s+(?=[A-Z0-9`""'*_\[(])(.*)$")]
    private static partial Regex FirstSentencePattern();
}
