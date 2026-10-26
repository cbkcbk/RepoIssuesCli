namespace RepoCli.Core.Features.Anchors;

public sealed class SourceText
{
    public const int ContextLinesBefore = 2;

    readonly string[] normalized;
    readonly int[] contentLines;
    readonly int[] firstContentFrom;

    SourceText(string[] lines)
    {
        Lines = lines;
        normalized = [.. lines.Select(Fingerprint.Normalize)];
        contentLines = [.. Enumerable.Range(1, lines.Length).Where(line => normalized[line - 1].Length > 0)];
        firstContentFrom = new int[lines.Length + 1];
        var next = contentLines.Length;
        firstContentFrom[lines.Length] = next;
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (normalized[index].Length > 0)
                next--;
            firstContentFrom[index] = next;
        }
    }

    public IReadOnlyList<string> Lines { get; }

    public int LineCount => Lines.Count;

    public static SourceText Parse(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return new SourceText(lines[^1].Length == 0 ? lines[..^1] : lines);
    }

    public static SourceText Load(string path) => Parse(File.ReadAllText(path));

    public bool IsBlank(int line) => normalized[CheckLine(line) - 1].Length == 0;

    public int? ContentLineAtOrAfter(int line)
    {
        var index = firstContentFrom[CheckLine(line) - 1];
        return index < contentLines.Length ? contentLines[index] : null;
    }

    public int? ContentLineAtOrBefore(int line)
    {
        var index = firstContentFrom[CheckLine(line)] - 1;
        return index >= 0 ? contentLines[index] : null;
    }

    public int SpanBetween(int line, int endLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(endLine, line);
        return firstContentFrom[CheckLine(endLine)] - firstContentFrom[CheckLine(line) - 1];
    }

    public int EndLine(int line, int span)
    {
        var (start, end) = BodyBounds(line, span);
        return end == start ? line : contentLines[end - 1];
    }

    public string ContextHash(int line)
    {
        var start = firstContentFrom[CheckLine(line) - 1];
        var before = contentLines[Math.Max(0, start - ContextLinesBefore)..start].Select(content => normalized[content - 1]);
        return Fingerprint.Of([.. before, normalized[line - 1]]);
    }

    public string BodyHash(int line, int span)
    {
        var (start, end) = BodyBounds(line, span);
        return Fingerprint.Of(contentLines[start..end].Select(content => normalized[content - 1]));
    }

    (int Start, int End) BodyBounds(int line, int span)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(span);
        var start = firstContentFrom[CheckLine(line) - 1];
        return (start, Math.Min(start + span, contentLines.Length));
    }

    int CheckLine(int line)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(line, LineCount);
        return line;
    }
}
