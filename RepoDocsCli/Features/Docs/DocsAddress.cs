namespace RepoDocsCli.Features.Docs;

public sealed record DocsAddress(string Feature, string? Entry = null)
{
    public bool IsEntry => Entry is not null;

    public bool IsRepo => Feature == FeatureId.RepoFileStem;

    public static DocsAddress Parse(string text)
    {
        var parts = text.Trim().Split(EntryId.Separator);
        var feature = parts[0];
        var featureValid = FeatureId.IsValid(feature) || feature == FeatureId.RepoFileStem;

        return parts switch
        {
            [_] when featureValid => new DocsAddress(feature),
            [_, var entry] when featureValid && EntryId.IsValid(entry) => new DocsAddress(feature, entry),
            _ => throw new UsageException(
                $"'{text}' is not a valid address. Use <feature> or <feature>{EntryId.Separator}<entry>, and {FeatureId.RepoFileStem}{EntryId.Separator}<entry> for repo.yaml entries; ids are lowercase kebab-case."),
        };
    }

    public override string ToString() => Entry is null ? Feature : EntryId.Address(Feature, Entry);
}