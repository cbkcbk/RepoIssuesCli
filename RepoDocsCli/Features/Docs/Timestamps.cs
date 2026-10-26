using RepoCli.Core.Features.Anchors;

namespace RepoDocsCli.Features.Docs;

public static class Timestamps
{
    public static Feature Stamp(Feature? previous, Feature current, DateTimeOffset now)
    {
        var entries = Stamp(previous?.Entries, current.Entries, now);
        var (created, updated) = Stamp(previous?.Created, previous?.Updated, current.Created, current.Updated,
            previous is not null && !SameContent(previous, current), now);

        if (created == current.Created && updated == current.Updated && entries is null)
            return current;

        return current with { Created = created, Updated = updated, Entries = entries ?? current.Entries };
    }

    public static RepoDocs Stamp(RepoDocs? previous, RepoDocs current, DateTimeOffset now) =>
        Stamp(previous?.Entries, current.Entries, now) is { } entries ? current with { Entries = entries } : current;

    public static FeatureEntry Stamp(FeatureEntry? previous, FeatureEntry current, DateTimeOffset now)
    {
        var (created, updated) = Stamp(previous?.Created, previous?.Updated, current.Created, current.Updated,
            previous is not null && !SameContent(previous, current), now);

        return created == current.Created && updated == current.Updated
            ? current
            : current with { Created = created, Updated = updated };
    }

    public static FeatureEntry Touch(FeatureEntry entry, DateTimeOffset now) => entry with { Updated = now };

    public static Feature Touch(Feature feature, DateTimeOffset now) => feature with { Updated = now };

    public static FeatureEntry MarkRead(FeatureEntry entry, DateOnly today) =>
        entry.Read >= today ? entry : entry with { Read = today };

    public static DateTimeOffset? LastChange(FeatureEntry entry) => entry.Updated ?? entry.Created;

    public static DateTimeOffset? LastChange(Feature feature) => feature.Updated ?? feature.Created;

    public static bool SameContent(Feature left, Feature right) =>
        left.Status == right.Status && SameText(left.Summary, right.Summary) && SameHints(left.When, right.When);

    public static bool SameContent(FeatureEntry left, FeatureEntry right) =>
        left.Kind == right.Kind && SameText(left.Summary, right.Summary) && SameText(left.Body, right.Body) && SameHints(left.When, right.When);

    public static bool SameHints(WhenHints? left, WhenHints? right) =>
        (left?.Paths ?? []).SequenceEqual(right?.Paths ?? [], StringComparer.Ordinal)
        && (left?.Keywords ?? []).SequenceEqual(right?.Keywords ?? [], StringComparer.Ordinal)
        && (left?.Anchors ?? []).SequenceEqual(right?.Anchors ?? [], AnchorContent.Comparer);

    static List<FeatureEntry>? Stamp(IReadOnlyList<FeatureEntry>? previous, List<FeatureEntry> current, DateTimeOffset now)
    {
        var stamped = current.Select(entry => Stamp(previous?.FirstOrDefault(old => old.Id == entry.Id), entry, now)).ToList();
        return stamped.SequenceEqual(current, ReferenceEqualityComparer.Instance) ? null : stamped;
    }

    static (DateTimeOffset? Created, DateTimeOffset? Updated) Stamp(
        DateTimeOffset? previousCreated, DateTimeOffset? previousUpdated,
        DateTimeOffset? created, DateTimeOffset? updated, bool contentChanged, DateTimeOffset now)
    {
        var lastChange = previousUpdated ?? previousCreated;
        var alreadyBumped = updated is { } value && (lastChange is null || value > lastChange);
        return (
            created ?? previousCreated ?? now,
            contentChanged && !alreadyBumped ? now : updated ?? previousUpdated);
    }

    static bool SameText(string? left, string? right) => (left ?? "").TrimEnd() == (right ?? "").TrimEnd();

    sealed class AnchorContent : IEqualityComparer<Anchor>
    {
        public static readonly AnchorContent Comparer = new();

        public bool Equals(Anchor? left, Anchor? right) =>
            left is not null && right is not null && left with { Line = 0 } == right with { Line = 0 };

        public int GetHashCode(Anchor anchor) => (anchor with { Line = 0 }).GetHashCode();
    }
}
