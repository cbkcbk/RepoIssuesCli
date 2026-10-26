namespace RepoDocsCli.Features.Docs;

public sealed record EntryOwner(string Id, Feature? Feature, RepoDocs? Repo)
{
    public static EntryOwner Of(Feature feature) => new(feature.Id, feature, null);

    public static EntryOwner Of(RepoDocs repo) => new(FeatureId.RepoFileStem, null, repo);

    public List<FeatureEntry> Entries => Feature?.Entries ?? Repo?.Entries ?? [];

    public string FileName => DocsStore.FileNameFor(Id);

    public FeatureEntry? Find(string entryId) => Entries.FirstOrDefault(entry => entry.Id == entryId);

    public FeatureEntry Require(string entryId) =>
        Find(entryId) ?? throw new EntryNotFoundException(EntryId.Address(Id, entryId));

    public EntryOwner WithEntries(List<FeatureEntry> entries) => Feature is { } feature
        ? this with { Feature = feature with { Entries = entries } }
        : this with { Repo = (Repo ?? new RepoDocs()) with { Entries = entries } };

    public EntryOwner Replace(string entryId, FeatureEntry? replacement) =>
        WithEntries(Entries
            .Where(entry => entry.Id != entryId || replacement is not null)
            .Select(entry => entry.Id == entryId ? replacement! : entry)
            .ToList());

    public DocsSnapshot ApplyTo(DocsSnapshot snapshot) => Feature is { } feature
        ? DocsChange.WithFeature(snapshot, Id, feature)
        : snapshot with { Repo = Repo ?? new RepoDocs() };

    public string Serialize() => Feature is { } feature ? DocsYaml.Serialize(feature) : DocsYaml.Serialize(Repo);
}
