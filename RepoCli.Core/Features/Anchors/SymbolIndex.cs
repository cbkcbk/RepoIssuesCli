namespace RepoCli.Core.Features.Anchors;

public sealed class SymbolIndex
{
    static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".kt", ".kts", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".py", ".pyi", ".go", ".rs"
    };

    SymbolIndex(IReadOnlyList<Declaration> declarations) => Declarations = declarations;

    /// <summary>Whether the scanner understands files with this path's extension.</summary>
    public static bool Supports(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    /// <summary>All detected declarations, ordered by line; nested ones follow their container.</summary>
    public IReadOnlyList<Declaration> Declarations { get; }

    public static SymbolIndex Scan(SourceText text) => new(SymbolScanner.Scan(text));

    /// <summary>The innermost declaration whose header-to-end range contains the 1-based <paramref name="line"/>.</summary>
    public Declaration? EnclosingAt(int line) => Declarations.LastOrDefault(declaration => declaration.Contains(line));

    public string? SymbolAt(int line) => EnclosingAt(line)?.Symbol;

    /// <summary>
    /// Exact symbol matches first; otherwise declarations whose symbol ends with the longest matching
    /// trailing part of <paramref name="symbol"/>, so <c>Old.Inner.Method</c> still finds <c>New.Inner.Method</c>.
    /// </summary>
    public IReadOnlyList<Declaration> Find(string symbol)
    {
        var exact = Declarations.Where(declaration => declaration.Symbol == symbol).ToList();
        if (exact.Count > 0)
            return exact;

        var segments = symbol.Split('.');
        for (var skip = 0; skip < segments.Length; skip++)
        {
            var suffix = string.Join('.', segments[skip..]);
            var matches = Declarations
                .Where(declaration => declaration.Symbol == suffix || declaration.Symbol.EndsWith('.' + suffix, StringComparison.Ordinal))
                .ToList();
            if (matches.Count > 0)
                return matches;
        }

        return [];
    }
}
