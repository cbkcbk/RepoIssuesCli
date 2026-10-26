namespace RepoCli.Core.Features.Anchors;

/// <param name="Line">1-based line of the declaration header.</param>
/// <param name="EndLine">1-based last line of the body, estimated from braces or indentation.</param>
/// <param name="Symbol">Dot-joined names of the enclosing declarations and this one, e.g. <c>Outer.Inner.Method</c>.</param>
public sealed record Declaration(string Name, SymbolKind Kind, int Line, int EndLine, string Symbol)
{
    public bool Contains(int line) => line >= Line && line <= EndLine;

    public bool IsType => Kind is not (SymbolKind.Method or SymbolKind.Function);
}
