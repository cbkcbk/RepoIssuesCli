namespace RepoDocsCli.Features.Docs;

public sealed partial class DocsStore
{
    public EntryOwner LoadOwner(string id)
    {
        if (id != FeatureId.RepoFileStem)
            return EntryOwner.Of(LoadFeature(id));

        var repo = File.Exists(RepoFile) && Read<RepoDocs>(RepoFile) is { } repoDocs ? Normalize(repoDocs) : new RepoDocs();
        return EntryOwner.Of(repo);
    }

    public EntryOwner Save(EntryOwner owner) =>
        owner.Feature is { } feature ? EntryOwner.Of(Save(feature)) : EntryOwner.Of(SaveRepo(owner.Repo ?? new RepoDocs()));
}
