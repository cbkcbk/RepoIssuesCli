using RepoCli.Core.Features.Anchors;

namespace RepoIssuesCli.Features.Issues;

public sealed record LocationFix(string Issue, string File, string From, string To, string Reason);

/// <summary>Anchors locations against the working tree and resolves them, reading each file and its symbols once.</summary>
public sealed class IssueLocations(string repoRoot)
{
    readonly Dictionary<string, (SourceText Text, SymbolIndex? Symbols)?> files = new(StringComparer.Ordinal);
    AnchorBatch? batch;

    /// <summary>An anchor when the file and line exist; otherwise the plain path and line, which validate reports.</summary>
    public SourceLocation Create(LocationInput input)
    {
        var plain = new SourceLocation(input.Path, input.Line, input.Column);
        if (input.Line is not { } line || Load(input.Path) is not ({ } text, var symbols) || line > text.LineCount)
            return plain;

        var end = Math.Min(input.EndLine ?? line, text.LineCount);
        var anchor = Anchor.Create(input.Path, text, line, end);
        if (anchor.Span == 0)
            return plain;

        var column = anchor.Line == line ? input.Column : null;
        return SourceLocation.From(anchor with { Symbol = symbols?.SymbolAt(anchor.Line) }, column);
    }

    /// <summary>Anchors legacy <c>path:line</c> locations that still point at an existing line.</summary>
    public Issue Upgrade(Issue issue)
    {
        if (issue.Locations.All(location => location.Anchor is not null || location.Line is null))
            return issue;

        var locations = issue.Locations
            .Select(location => location.Anchor is null && location.Line is not null
                ? Create(new LocationInput(location.Path, location.Line, location.Column))
                : location)
            .Distinct()
            .ToList();
        return issue with { Locations = locations };
    }

    public AnchorResolution Resolve(Anchor anchor) => (batch ??= new AnchorResolver(repoRoot).Batch()).Resolve(anchor);

    /// <summary>Rewrites the hints of confidently moved anchors; changed, broken and fuzzy results are left alone.</summary>
    public (Issue Issue, IReadOnlyList<LocationFix> Fixes) FixMoved(Issue issue)
    {
        var fixes = new List<LocationFix>();
        var locations = issue.Locations.Select(location =>
        {
            if (location.Anchor is not { } anchor)
                return location;

            var resolution = Resolve(anchor);
            if (AnchorResolver.Fix(resolution) is not { } moved)
                return location;

            var rehinted = location.Rehint(moved);
            fixes.Add(new LocationFix(issue.Title, IssueStore.FileNameFor(issue), location.ToString(), rehinted.ToString(), resolution.Reason));
            return rehinted;
        }).ToList();

        return fixes.Count == 0 ? (issue, fixes) : (issue with { Locations = locations }, fixes);
    }

    (SourceText Text, SymbolIndex? Symbols)? Load(string path)
    {
        if (files.TryGetValue(path, out var cached))
            return cached;

        var fullPath = Path.Combine(repoRoot, path);
        if (!File.Exists(fullPath))
            return files[path] = null;

        var text = SourceText.Load(fullPath);
        return files[path] = (text, SymbolIndex.Supports(path) ? SymbolIndex.Scan(text) : null);
    }
}
