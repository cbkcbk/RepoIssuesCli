namespace RepoCli.Core.Features.Anchors;

/// <summary>Resolves anchors against a snapshot: each file, its symbols, renames and git history are read once.</summary>
public sealed class AnchorBatch
{
    const int MaxRenameHops = 5;

    readonly string root;
    readonly Dictionary<string, AnchorFile?> files = new(StringComparer.Ordinal);
    readonly Dictionary<string, string?> renames = new(StringComparer.Ordinal);
    readonly Dictionary<string, AnchorHistory> histories = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, string>? workingRenames;

    internal AnchorBatch(string repoRoot) => root = repoRoot;

    public AnchorResolution Resolve(Anchor anchor)
    {
        if (Load(anchor.Path) is { } file)
            return Search(anchor, file) ?? Broken(anchor, "context, symbol and similar context not found");

        if (Rename(anchor.Path) is not { } newPath)
            return Broken(anchor, "file not found");

        return Load(newPath) is { } renamed && Search(anchor, renamed) is { } found
            ? found
            : Broken(anchor, $"file renamed to {newPath}, but the anchor was not found there");
    }

    AnchorResolution? Search(Anchor anchor, AnchorFile file)
    {
        if (ByContext(anchor, file) is { } found)
            return found;

        var origin = Origin(anchor);
        return BySymbol(anchor, file, origin) ?? ByFuzzy(anchor, file, origin);
    }

    static AnchorResolution? ByContext(Anchor anchor, AnchorFile file)
    {
        var contextLines = file.LinesWithContext(anchor.Context).ToList();
        if (contextLines.Count == 0)
            return null;

        var bodyLines = contextLines.Where(line => anchor.BodyMatches(file.Text, line)).ToList();
        var line = Nearest(bodyLines.Count > 0 ? bodyLines : contextLines, anchor.Line);
        return line == anchor.Line
            ? Found(anchor, file, line, AnchorSearch.Hint, "context matches at the hint")
            : Found(anchor, file, line, AnchorSearch.SameFile, "context found");
    }

    static AnchorResolution? BySymbol(Anchor anchor, AnchorFile file, AnchorOrigin? origin)
    {
        if (!file.IsSource || (anchor.Symbol ?? origin?.Symbol) is not { } symbol)
            return null;

        var declarations = file.Symbols.Find(symbol);
        if (declarations.Count == 0)
            return null;

        var offset = origin?.OffsetWithin(symbol);
        var bodyMatch = declarations
            .SelectMany(declaration => Enumerable.Range(declaration.Line, declaration.EndLine - declaration.Line + 1)
                .Where(line => line <= file.Text.LineCount && !file.Text.IsBlank(line) && anchor.BodyMatches(file.Text, line))
                .Select(line => (Declaration: declaration, Line: line)))
            .OrderBy(match => Math.Abs(match.Line - (offset is { } known ? match.Declaration.Line + known : anchor.Line)))
            .ThenBy(match => Math.Abs(match.Declaration.Line - anchor.Line))
            .Select(match => (int?)match.Line)
            .FirstOrDefault();

        if (bodyMatch is { } moved)
            return Found(anchor, file, moved, AnchorSearch.Symbol, $"symbol {symbol} found");

        int Expected(Declaration declaration) =>
            offset is { } known ? Math.Min(declaration.Line + known, declaration.EndLine) : declaration.Line;

        var nearest = declarations.MinBy(declaration => Math.Abs(Expected(declaration) - anchor.Line))!;
        var changed = file.Text.ContentLineAtOrAfter(Expected(nearest)) ?? nearest.Line;
        return Found(anchor, file, changed, AnchorSearch.Symbol, $"symbol {symbol} found");
    }

    static AnchorResolution? ByFuzzy(Anchor anchor, AnchorFile file, AnchorOrigin? origin)
    {
        if (origin is null || file.ContentLines.Count == 0)
            return null;

        var expected = origin.File.WindowTokens(origin.Line, anchor.Span);
        var best = file.ContentLines
            .Select(line => (Line: line, Similarity: ContextSimilarity.Of(expected, file.WindowTokens(line, anchor.Span))))
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => Math.Abs(candidate.Line - anchor.Line))
            .First();

        return best.Similarity >= ContextSimilarity.Threshold
            ? Found(anchor, file, best.Line, AnchorSearch.Fuzzy, $"similar context ({best.Similarity:P0})", best.Similarity)
            : null;
    }

    static AnchorResolution Found(Anchor anchor, AnchorFile file, int line, AnchorSearch step, string how, double? similarity = null)
    {
        var bodyMatches = anchor.BodyMatches(file.Text, line);
        var renamed = file.Path != anchor.Path;
        if (step == AnchorSearch.Hint && bodyMatches && !renamed)
            return new AnchorResolution(AnchorState.Ok, anchor, anchor, step, how);

        var symbol = anchor.Symbol is not null && file.IsSource ? file.Symbols.SymbolAt(line) ?? anchor.Symbol : anchor.Symbol;
        var resolved = new Anchor(file.Path, line, anchor.Span, file.Text.ContextHash(line), file.Text.BodyHash(line, anchor.Span), symbol);
        var foundBy = renamed && step != AnchorSearch.Fuzzy ? AnchorSearch.Rename : step;
        var reason = (renamed ? $"file renamed to {file.Path}; " : "") + $"{how} at line {line}" + (bodyMatches ? "" : "; body differs");
        return new AnchorResolution(bodyMatches ? AnchorState.Moved : AnchorState.Changed, anchor, resolved, foundBy, reason, similarity);
    }

    static AnchorResolution Broken(Anchor anchor, string reason) =>
        new(AnchorState.Broken, anchor, null, AnchorSearch.None, reason);

    static int Nearest(IEnumerable<int> lines, int hint) => lines.MinBy(line => Math.Abs(line - hint));

    AnchorOrigin? Origin(Anchor anchor)
    {
        if (!histories.TryGetValue(anchor.Path, out var history))
            histories[anchor.Path] = history = new AnchorHistory(root, anchor.Path);

        foreach (var version in history.Versions())
        {
            var lines = version.LinesWithContext(anchor.Context).Where(line => anchor.BodyMatches(version.Text, line)).ToList();
            if (lines.Count > 0)
                return new AnchorOrigin(version, Nearest(lines, anchor.Line));
        }

        return null;
    }

    AnchorFile? Load(string path)
    {
        if (!files.TryGetValue(path, out var file))
        {
            var fullPath = Path.Combine(root, path);
            files[path] = file = File.Exists(fullPath) ? new AnchorFile(path, SourceText.Load(fullPath)) : null;
        }

        return file;
    }

    string? Rename(string path)
    {
        if (renames.TryGetValue(path, out var cached))
            return cached;

        workingRenames ??= AnchorGit.WorkingRenames(root);
        string? found = null;
        var current = path;
        for (var hop = 0; hop < MaxRenameHops; hop++)
        {
            if ((workingRenames.GetValueOrDefault(current) ?? AnchorGit.CommittedRename(root, current)) is not { } next)
                break;
            if (File.Exists(Path.Combine(root, next)))
            {
                found = next;
                break;
            }

            current = next;
        }

        return renames[path] = found;
    }
}
