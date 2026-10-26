namespace RepoCli.Core.Features.Anchors;

/// <param name="Line">1-based hint for the first line of the span.</param>
/// <param name="Span">Number of non-blank lines covered, starting at <paramref name="Line"/>.</param>
/// <param name="Context">Hash of the start line and the non-blank lines just before it.</param>
/// <param name="Body">Hash of the span's non-blank lines.</param>
public sealed record Anchor(string Path, int Line, int Span, string Context, string Body, string? Symbol = null)
{
    public static Anchor Create(string path, SourceText text, int line, int? endLine = null, string? symbol = null)
    {
        var end = endLine ?? line;
        ArgumentOutOfRangeException.ThrowIfLessThan(end, line, nameof(endLine));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, text.LineCount, nameof(endLine));

        var start = text.ContentLineAtOrAfter(line) ?? text.ContentLineAtOrBefore(line) ?? line;
        var span = text.SpanBetween(start, Math.Max(start, end));
        return new Anchor(path.Replace('\\', '/'), start, span, text.ContextHash(start), text.BodyHash(start, span), symbol);
    }

    public static Anchor Load(string repoRoot, string path, int line, int? endLine = null, string? symbol = null) =>
        Create(path, SourceText.Load(System.IO.Path.Combine(repoRoot, path)), line, endLine, symbol);

    public bool ContextMatches(SourceText text, int line) => text.ContextHash(line) == Context;

    public bool BodyMatches(SourceText text, int line) => text.BodyHash(line, Span) == Body;

    public int EndLine(SourceText text) => text.EndLine(Line, Span);
}
