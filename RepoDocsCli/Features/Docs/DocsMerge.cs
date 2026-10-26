using YamlDotNet.Core;

namespace RepoDocsCli.Features.Docs;

public static class DocsMerge
{
    public static string? Resolve(string fileName, string text)
    {
        if (ConflictMarkers.Split(text) is not { } sides)
            return null;

        try
        {
            return Path.GetFileNameWithoutExtension(fileName) switch
            {
                FeatureId.ConfigFileStem => null,
                FeatureId.RepoFileStem => DocsYaml.Serialize(Merge(
                    ParseOptional<RepoDocs>(sides.Ancestor, DocsStore.Normalize),
                    Parse<RepoDocs>(sides.Ours, DocsStore.Normalize),
                    Parse<RepoDocs>(sides.Theirs, DocsStore.Normalize))),
                _ => DocsYaml.Serialize(Merge(
                    ParseOptional<Feature>(sides.Ancestor, DocsStore.Normalize),
                    Parse<Feature>(sides.Ours, DocsStore.Normalize),
                    Parse<Feature>(sides.Theirs, DocsStore.Normalize))),
            };
        }
        catch (YamlException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public static Feature Merge(Feature? ancestor, Feature ours, Feature theirs)
    {
        var winner = Pick(ancestor, ours, theirs, Timestamps.SameContent, Timestamps.LastChange);
        return winner with
        {
            Created = Earliest(ours.Created, theirs.Created),
            Updated = Latest(ours.Updated, theirs.Updated),
            Entries = MergeEntries(ancestor?.Entries, ours.Entries, theirs.Entries),
        };
    }

    public static RepoDocs Merge(RepoDocs? ancestor, RepoDocs ours, RepoDocs theirs) => new()
    {
        Summary = ancestor is not null && ours.Summary == ancestor.Summary ? theirs.Summary : ours.Summary,
        Entries = MergeEntries(ancestor?.Entries, ours.Entries, theirs.Entries),
    };

    public static FeatureEntry Merge(FeatureEntry? ancestor, FeatureEntry ours, FeatureEntry theirs)
    {
        var winner = Pick(ancestor, ours, theirs, Timestamps.SameContent, Timestamps.LastChange);
        return winner with
        {
            Created = Earliest(ours.Created, theirs.Created),
            Updated = Latest(ours.Updated, theirs.Updated),
            Read = Latest(ours.Read, theirs.Read),
        };
    }

    public static List<FeatureEntry> MergeEntries(IReadOnlyList<FeatureEntry>? ancestor, IReadOnlyList<FeatureEntry> ours, IReadOnlyList<FeatureEntry> theirs)
    {
        var merged = new List<FeatureEntry>();
        var ids = ours.Select(entry => entry.Id).Concat(theirs.Select(entry => entry.Id)).Distinct();
        foreach (var id in ids)
        {
            var original = ancestor?.FirstOrDefault(entry => entry.Id == id);
            var mine = ours.FirstOrDefault(entry => entry.Id == id);
            var other = theirs.FirstOrDefault(entry => entry.Id == id);

            var result = (mine, other) switch
            {
                ({ } left, { } right) => Merge(original, left, right),
                _ when (mine ?? other) is { } only => DeletedOnOtherSide(original, only) ? null : only,
                _ => null,
            };
            if (result is not null)
                merged.Add(result);
        }

        return merged;
    }

    static bool DeletedOnOtherSide(FeatureEntry? original, FeatureEntry remaining) =>
        original is not null && Timestamps.SameContent(original, remaining);

    static T Pick<T>(T? ancestor, T ours, T theirs, Func<T, T, bool> sameContent, Func<T, DateTimeOffset?> lastChange) where T : class
    {
        if (ancestor is not null && sameContent(ours, ancestor))
            return theirs;
        if (ancestor is not null && sameContent(theirs, ancestor))
            return ours;

        return lastChange(theirs) > lastChange(ours) || (lastChange(ours) is null && lastChange(theirs) is not null) ? theirs : ours;
    }

    static T? Earliest<T>(T? left, T? right) where T : struct, IComparable<T> =>
        left is { } a && right is { } b ? (a.CompareTo(b) <= 0 ? a : b) : left ?? right;

    static T? Latest<T>(T? left, T? right) where T : struct, IComparable<T> =>
        left is { } a && right is { } b ? (a.CompareTo(b) >= 0 ? a : b) : left ?? right;

    static T Parse<T>(string yaml, Func<T, T> normalize) where T : class =>
        DocsYaml.Deserialize<T>(yaml) is { } value ? normalize(value) : throw new InvalidDataException("Empty conflict side.");

    static T? ParseOptional<T>(string? yaml, Func<T, T> normalize) where T : class =>
        yaml is null ? null : DocsYaml.Deserialize<T>(yaml) is { } value ? normalize(value) : null;
}
