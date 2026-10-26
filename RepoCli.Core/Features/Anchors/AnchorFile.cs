namespace RepoCli.Core.Features.Anchors;

/// <summary>A file version with lazily computed per-line context hashes, tokens and symbols.</summary>
sealed class AnchorFile
{
    static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".java", ".kt", ".kts", ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".py", ".pyi", ".go", ".rs"
    };

    readonly int[] contentLines;
    string?[]? contextHashes;
    string[][]? tokens;
    SymbolIndex? symbols;

    public AnchorFile(string path, SourceText text)
    {
        Path = path;
        Text = text;
        contentLines = [.. Enumerable.Range(1, text.LineCount).Where(line => !text.IsBlank(line))];
    }

    public string Path { get; }

    public SourceText Text { get; }

    public IReadOnlyList<int> ContentLines => contentLines;

    public bool IsSource => SourceExtensions.Contains(System.IO.Path.GetExtension(Path));

    public SymbolIndex Symbols => symbols ??= SymbolIndex.Scan(Text);

    public IEnumerable<int> LinesWithContext(string context)
    {
        contextHashes ??= [.. contentLines.Select(Text.ContextHash)];
        for (var position = 0; position < contentLines.Length; position++)
            if (contextHashes[position] == context)
                yield return contentLines[position];
    }

    /// <summary>Token counts of the context lines before <paramref name="line"/> plus the span starting there.</summary>
    public Dictionary<string, int> WindowTokens(int line, int span)
    {
        tokens ??= [.. contentLines.Select(content => ContextSimilarity.Tokenize(Text.Lines[content - 1]))];
        var position = Array.BinarySearch(contentLines, line);
        if (position < 0)
            position = ~position;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var end = Math.Min(contentLines.Length, position + Math.Max(span, 1));
        for (var index = Math.Max(0, position - SourceText.ContextLinesBefore); index < end; index++)
            foreach (var token in tokens[index])
                counts[token] = counts.GetValueOrDefault(token) + 1;
        return counts;
    }
}
