using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public sealed record FeatureView(
    string Feature,
    FeatureStatus? Status,
    string? Summary,
    WhenHints? When,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    IReadOnlyList<EntryView> Entries)
{
    public static FeatureView Create(Feature feature, bool full, PlanLookup plans) => new(
        feature.Id,
        feature.Status,
        feature.Summary,
        feature.When,
        feature.Created,
        feature.Updated,
        feature.Entries.Select(entry => EntryView.Create(feature.Id, entry, full, plans)).ToList());

    public static FeatureView Create(RepoDocs repo, bool full, PlanLookup plans) => new(
        FeatureId.RepoFileStem,
        null,
        repo.Summary,
        null,
        null,
        null,
        repo.Entries.Select(entry => EntryView.Create(FeatureId.RepoFileStem, entry, full, plans)).ToList());
}

public sealed record FeatureListItem(string Feature, FeatureStatus Status, string? Summary, int EntryCount)
{
    public static FeatureListItem Create(Feature feature) => new(feature.Id, feature.Status, feature.Summary, feature.Entries.Count);
}
