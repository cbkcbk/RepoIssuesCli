using System.Text.RegularExpressions;
using RepoDocsCli.Features.ClaudeMd;

namespace RepoDocsCli.Features.Import;

public sealed record MarkdownLine(int Number, string Text);

public sealed record MarkdownSection(IReadOnlyList<string> Headings, int Line, IReadOnlyList<MarkdownLine> Content, bool HasSubsections)
{
    public string? Heading => Headings.Count > 0 ? Headings[^1] : null;
}

public sealed record MarkdownDocument(IReadOnlyList<MarkdownSection> Sections, IReadOnlyList<MarkdownLine> Imports);

public static partial class MarkdownReader
{
    public static MarkdownDocument Read(string text)
    {
        var lines = text.TrimStart('﻿').ReplaceLineEndings("\n").Split('\n');
        var sections = new List<SectionBuilder> { new(0, [], 0) };
        var imports = new List<MarkdownLine>();
        var path = new List<(int Level, string Text)>();
        string? fence = null;
        var inGeneratedBlock = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = new MarkdownLine(index + 1, lines[index]);

            if (fence is null)
            {
                if (inGeneratedBlock)
                {
                    inGeneratedBlock = !line.Text.StartsWith(ClaudeMdBlock.EndMarker, StringComparison.Ordinal);
                    continue;
                }

                if (line.Text.StartsWith(ClaudeMdBlock.BeginMarker, StringComparison.Ordinal))
                {
                    inGeneratedBlock = true;
                    continue;
                }

                if (HeadingPattern().Match(line.Text) is { Success: true } heading)
                {
                    var level = heading.Groups[1].Length;
                    path.RemoveAll(entry => entry.Level >= level);
                    path.Add((level, HeadingText(heading.Groups[2].Value)));
                    sections.Add(new SectionBuilder(level, path.Select(entry => entry.Text).ToList(), line.Number));
                    continue;
                }

                if (ImportPattern().IsMatch(line.Text))
                {
                    imports.Add(line with { Text = line.Text.Trim() });
                    continue;
                }
            }

            fence = NextFence(fence, line.Text);
            sections[^1].Content.Add(line);
        }

        var built = sections
            .Select((section, index) => new MarkdownSection(
                section.Headings,
                section.Line,
                section.Content,
                section.Level > 0 && index + 1 < sections.Count && sections[index + 1].Level > section.Level))
            .ToList();
        return new MarkdownDocument(built, imports);
    }

    static string? NextFence(string? open, string line)
    {
        if (FencePattern().Match(line) is not { Success: true } match)
            return open;

        var marker = match.Groups[1].Value;
        if (open is null)
            return marker;

        var closes = marker[0] == open[0] && marker.Length >= open.Length && match.Groups[2].Value.Trim().Length == 0;
        return closes ? null : open;
    }

    static string HeadingText(string text)
    {
        var withoutClosing = text.TrimEnd('#');
        return withoutClosing.Length < text.Length && (withoutClosing.Length == 0 || withoutClosing[^1] is ' ' or '\t')
            ? withoutClosing.Trim()
            : text.Trim();
    }

    sealed record SectionBuilder(int Level, List<string> Headings, int Line)
    {
        public List<MarkdownLine> Content { get; } = [];
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*))?$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^\s*(?:[-*+]\s+)?@[^\s@]+\s*$")]
    private static partial Regex ImportPattern();
}
