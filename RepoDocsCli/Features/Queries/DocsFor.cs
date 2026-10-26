using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public sealed record ForOptions(bool Plans = false, bool AllKinds = false, bool Full = false);

public sealed record FeatureRef(string Feature, FeatureStatus Status, string? Summary);

public sealed record ForResult(string Path, IReadOnlyList<FeatureRef> Features, IReadOnlyList<EntryView> Entries);

public static class DocsFor
{
    public const string HistoricalKind = "decision";
    public const int ShortBodyLength = 600;

    public static ForResult Query(DocsSnapshot snapshot, string path, ForOptions options, PlanLookup plans)
    {
        var entries = snapshot.Repo.Entries
            .Where(entry => IsCurrentState(entry.Kind, featureActive: false, options))
            .Select(entry => View(FeatureId.RepoFileStem, entry, EntryMatch.Repo, options, plans))
            .ToList();
        var features = new List<FeatureRef>();

        foreach (var feature in snapshot.Features.OrderBy(feature => feature.Id, StringComparer.Ordinal))
        {
            var featureMatches = PathMatch.Matches(feature.When, path);
            var active = feature.Status == FeatureStatus.InProgress;
            var matched = feature.Entries
                .Where(entry => IsCurrentState(entry.Kind, active, options))
                .Select(entry => MatchOf(entry, featureMatches, path) is { } match ? View(feature.Id, entry, match, options, plans) : null)
                .OfType<EntryView>()
                .ToList();

            if (featureMatches || matched.Count > 0)
                features.Add(new FeatureRef(feature.Id, feature.Status, feature.Summary));
            entries.AddRange(matched);
        }

        return new ForResult(path, features, entries);
    }

    public static bool IsCurrentState(string kind, bool featureActive, ForOptions options) => kind switch
    {
        HistoricalKind => options.AllKinds,
        PlanStep.EntryKind => options.AllKinds || options.Plans || featureActive,
        _ => true,
    };

    public static string RepoRelative(string root, string path, string currentDirectory)
    {
        var baseDirectory = IsUnder(root, currentDirectory) ? currentDirectory : root;
        var full = Path.GetFullPath(path, baseDirectory);
        if (!IsUnder(root, full))
            throw new UsageException($"'{path}' is outside the repository root {root}.");

        var relative = Path.GetRelativePath(root, full).Replace('\\', '/').TrimEnd('/');
        return relative == "." ? "" : relative;
    }

    static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "."
               || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    static EntryMatch? MatchOf(FeatureEntry entry, bool featureMatches, string path)
    {
        if (PathMatch.HasLocation(entry.When))
            return PathMatch.Matches(entry.When, path) ? EntryMatch.Entry : null;
        return featureMatches ? EntryMatch.Feature : null;
    }

    static EntryView View(string owner, FeatureEntry entry, EntryMatch match, ForOptions options, PlanLookup plans) =>
        EntryView.Create(owner, entry, options.Full || IsShort(entry.Body), plans, match);

    static bool IsShort(string? body) => body is null || body.Length <= ShortBodyLength;
}
