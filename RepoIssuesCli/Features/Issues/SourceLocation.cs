using System.Text.Json.Serialization;
using RepoCli.Core.Features.Anchors;

namespace RepoIssuesCli.Features.Issues;

/// <summary>A path with an optional line; anchored when it also has a span and fingerprints.</summary>
public sealed record SourceLocation(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] int? Line = null,
    [property: JsonPropertyOrder(6)] int? Column = null)
{
    [JsonPropertyOrder(2)]
    public int? Span { get; init; }

    [JsonPropertyOrder(3)]
    public string? Symbol { get; init; }

    [JsonPropertyOrder(4)]
    public string? Context { get; init; }

    [JsonPropertyOrder(5)]
    public string? Body { get; init; }

    [JsonIgnore]
    public Anchor? Anchor => (Line, Span, Context, Body) is ({ } line, { } span, { } context, { } body)
        ? new Anchor(Path, line, span, context, body, Symbol)
        : null;

    public static SourceLocation From(Anchor anchor, int? column = null) => new(anchor.Path, anchor.Line, column)
    {
        Span = anchor.Span,
        Symbol = anchor.Symbol,
        Context = anchor.Context,
        Body = anchor.Body,
    };

    public SourceLocation Rehint(Anchor anchor) => From(anchor, Column);

    public bool Covers(LocationInput removed) =>
        removed.Path == Path && (removed.Line is null || (removed.Line == Line && (removed.Column is null || removed.Column == Column)));

    public override string ToString() => (Line, Column) switch
    {
        (null, _) => Path,
        (_, null) => $"{Path}:{Line}",
        _ => $"{Path}:{Line}:{Column}",
    };

    /// <summary>The location with its span and enclosing symbol, e.g. <c>src/a.cs:12 (3 lines) in Calculator.Add</c>.</summary>
    public string Describe()
    {
        var extra = Span is { } span and > 1 ? $" ({span} lines)" : "";
        var symbol = Symbol is null ? "" : $" in {Symbol}";
        return $"{this}{extra}{symbol}";
    }
}
