namespace RepoCli.Core.Features.Anchors;

/// <summary>The past file version and line where the anchor's context and body both matched.</summary>
sealed record AnchorOrigin(AnchorFile File, int Line)
{
    public string? Symbol => File.IsSource ? File.Symbols.SymbolAt(Line) : null;

    public int? OffsetWithin(string symbol) =>
        File.IsSource && File.Symbols.Find(symbol).LastOrDefault(declaration => declaration.Contains(Line)) is { } declaration
            ? Line - declaration.Line
            : null;
}
