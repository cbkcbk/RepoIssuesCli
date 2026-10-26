using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using YamlDotNet.Core;

namespace RepoDocsCli.Features.Affected;

public enum AffectedReason
{
    AnchorEdited,
    AnchorRenamed,
    AnchorDeleted,
    PathAdded,
    PathModified,
    PathRenamed,
    PathDeleted,
}

public sealed record AffectedMatch(string Path, string? Anchor, string? PreviousPath, AffectedReason Reason);

public sealed record AffectedEntry(string Address, string Kind, string? Summary, IReadOnlyList<AffectedMatch> Matched, string Suggestion);

public sealed record AffectedResult(string Since, IReadOnlyList<AffectedEntry> Entries);

/// <summary>Docs whose hints the diff hit, whose content is unchanged since the ref and whose last change predates the hit files'.</summary>
public sealed class DocsAffected
{
    public const string FeatureKind = "feature";
    static readonly ForOptions CurrentState = new();

    readonly GitDiff diff;
    readonly List<FileChange> code;
    readonly Dictionary<string, SourceText?> previousTexts = new(StringComparer.OrdinalIgnoreCase);

    DocsAffected(GitDiff diff)
    {
        this.diff = diff;
        code = [.. diff.Files.Where(file => !file.Paths.Any(IsDocsPath))];
    }

    public static AffectedResult Find(string root, DocsSnapshot snapshot, string since) =>
        new(since, new DocsAffected(GitDiff.Read(root, since)).Find(snapshot));

    public static string Suggestion(string address) =>
        $"Review it, then run 'docs edit {address}', or 'docs touch {address}' if it is still correct.";

    List<AffectedEntry> Find(DocsSnapshot snapshot)
    {
        var found = new List<AffectedEntry>();

        var repoBefore = new Lazy<Before<RepoDocs>>(() => Previous<RepoDocs>(FeatureId.RepoFileStem, DocsStore.Normalize));
        foreach (var entry in snapshot.Repo.Entries.Where(entry => PathMatch.HasLocation(entry.When) && DocsFor.IsCurrentState(entry.Kind, false, CurrentState)))
            Check(found, FeatureId.RepoFileStem, entry, Hits(entry.When!), repoBefore, repo => repo.Entries);

        foreach (var feature in snapshot.Features)
        {
            var before = new Lazy<Before<Feature>>(() => Previous<Feature>(feature.Id, DocsStore.Normalize));
            var featureHits = Hits(feature.When);
            if (featureHits.Count > 0
                && !before.Value.Edited(previous => !Timestamps.SameContent(previous, feature))
                && IsStale(Timestamps.LastChange(feature), featureHits))
                found.Add(new AffectedEntry(feature.Id, FeatureKind, feature.Summary, featureHits, Suggestion(feature.Id)));

            var active = feature.Status == FeatureStatus.InProgress;
            foreach (var entry in feature.Entries.Where(entry => DocsFor.IsCurrentState(entry.Kind, active, CurrentState)))
                Check(found, feature.Id, entry, PathMatch.HasLocation(entry.When) ? Hits(entry.When!) : featureHits, before, previous => previous.Entries);
        }

        return [.. found.OrderByDescending(entry => entry.Matched.Any(match => match.Anchor is not null))];
    }

    void Check<T>(List<AffectedEntry> found, string owner, FeatureEntry entry, List<AffectedMatch> hits, Lazy<Before<T>> before, Func<T, List<FeatureEntry>> entries)
        where T : class
    {
        if (hits.Count == 0
            || before.Value.Edited(previous => entries(previous).FirstOrDefault(old => old.Id == entry.Id) is not { } old || !Timestamps.SameContent(old, entry))
            || !IsStale(Timestamps.LastChange(entry), hits))
            return;

        var address = EntryId.Address(owner, entry.Id);
        found.Add(new AffectedEntry(address, entry.Kind, entry.Summary, hits, Suggestion(address)));
    }

    bool IsStale(DateTimeOffset? lastChange, IEnumerable<AffectedMatch> hits) =>
        lastChange is not { } last || hits.Any(hit => diff.ChangeTime(hit.Path) > last);

    List<AffectedMatch> Hits(WhenHints hints)
    {
        var hits = hints.Anchors.Select(AnchorHit).OfType<AffectedMatch>().ToList();
        foreach (var file in code)
        {
            if (hits.Any(hit => SamePath(hit.Path, file.Path)) || !hints.Paths.Any(hint => file.Paths.Any(path => PathMatch.Covers(hint, path))))
                continue;

            hits.Add(new AffectedMatch(file.Path, null, file.PreviousPath, file.Kind switch
            {
                ChangeKind.Added => AffectedReason.PathAdded,
                ChangeKind.Deleted => AffectedReason.PathDeleted,
                ChangeKind.Renamed => AffectedReason.PathRenamed,
                _ => AffectedReason.PathModified,
            }));
        }

        return hits;
    }

    AffectedMatch? AnchorHit(Anchor anchor)
    {
        var path = PathMatch.Normalize(anchor.Path);
        var location = $"{path}:{anchor.Line}";
        return code.FirstOrDefault(file => file.Paths.Any(candidate => SamePath(candidate, path))) switch
        {
            { Kind: ChangeKind.Deleted } file => new AffectedMatch(file.Path, location, null, AffectedReason.AnchorDeleted),
            { Kind: ChangeKind.Renamed } file => new AffectedMatch(file.Path, location, file.PreviousPath, AffectedReason.AnchorRenamed),
            { Kind: ChangeKind.Modified } file when SpanEdited(anchor, file.Path) => new AffectedMatch(file.Path, location, null, AffectedReason.AnchorEdited),
            _ => null,
        };
    }

    bool SpanEdited(Anchor anchor, string path)
    {
        var ranges = diff.HunksOf(path);
        if (ranges.Count == 0)
            return false;
        if (PreviousText(path) is not { LineCount: > 0 } text)
            return true;

        var start = Locate(anchor, text);
        var end = text.EndLine(start, anchor.Span);
        return ranges.Any(range => range.Touches(start, end));
    }

    static int Locate(Anchor anchor, SourceText text)
    {
        var hint = Math.Clamp(anchor.Line, 1, text.LineCount);
        if (!text.IsBlank(hint) && anchor.BodyMatches(text, hint))
            return hint;

        var candidates = Enumerable.Range(1, text.LineCount).Where(line => !text.IsBlank(line)).OrderBy(line => Math.Abs(line - hint)).ToList();
        return candidates.FirstOrDefault(line => anchor.BodyMatches(text, line)) is var body and > 0 ? body
            : candidates.FirstOrDefault(line => anchor.ContextMatches(text, line)) is var context and > 0 ? context
            : hint;
    }

    SourceText? PreviousText(string path)
    {
        if (!previousTexts.TryGetValue(path, out var text))
            previousTexts[path] = text = diff.Show(path) is { } content ? SourceText.Parse(content) : null;
        return text;
    }

    Before<T> Previous<T>(string stem, Func<T, T> normalize) where T : class
    {
        var path = $"{RepoLocator.DocsFolderName}/{DocsStore.FileNameFor(stem)}";
        switch (diff.Files.FirstOrDefault(file => SamePath(file.Path, path)))
        {
            case null:
                return new Before<T>(DocsFileState.Unchanged, null);
            case { Kind: ChangeKind.Added }:
                return new Before<T>(DocsFileState.New, null);
            case var file:
                try
                {
                    var previous = diff.Show(file.PreviousPath ?? file.Path) is { } text ? DocsYaml.Deserialize<T>(text) : null;
                    return new Before<T>(DocsFileState.Changed, previous is null ? null : normalize(previous));
                }
                catch (YamlException)
                {
                    return new Before<T>(DocsFileState.Changed, null);
                }
        }
    }

    static bool IsDocsPath(string path) => path.StartsWith(RepoLocator.DocsFolderName + "/", StringComparison.OrdinalIgnoreCase);

    static bool SamePath(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    enum DocsFileState
    {
        Unchanged,
        New,
        Changed,
    }

    /// <summary>A docs file at the base ref; a changed file that can't be read counts as unedited.</summary>
    sealed record Before<T>(DocsFileState State, T? Value) where T : class
    {
        public bool Edited(Func<T, bool> differs) => State switch
        {
            DocsFileState.New => true,
            DocsFileState.Changed => Value is { } value && differs(value),
            _ => false,
        };
    }
}
