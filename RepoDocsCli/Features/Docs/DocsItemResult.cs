namespace RepoDocsCli.Features.Docs;

public sealed record DocsItemResult(string Address, string File, Feature? Feature = null, FeatureEntry? Entry = null, RepoDocs? Repo = null)
{
    public static DocsItemResult For(Feature feature) => new(feature.Id, FilePath(feature.Id), Feature: feature);

    public static DocsItemResult For(string owner, FeatureEntry entry) => new(EntryId.Address(owner, entry.Id), FilePath(owner), Entry: entry);

    public static DocsItemResult For(RepoDocs repo) => new(FeatureId.RepoFileStem, FilePath(FeatureId.RepoFileStem), Repo: repo);

    static string FilePath(string stem) => $"{RepoLocator.DocsFolderName}/{DocsStore.FileNameFor(stem)}";
}
