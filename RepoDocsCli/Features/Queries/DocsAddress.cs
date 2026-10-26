using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public sealed record DocsAddress(string Owner, string? Entry)
{
    public bool IsRepo => Owner == FeatureId.RepoFileStem;

    public override string ToString() => Entry is null ? Owner : EntryId.Address(Owner, Entry);

    public static DocsAddress Parse(string text)
    {
        var parts = text.Trim().Split(EntryId.Separator);
        var owner = parts[0];
        var ownerValid = FeatureId.IsValid(owner) || owner == FeatureId.RepoFileStem;

        return parts switch
        {
            [_] when ownerValid => new DocsAddress(owner, null),
            [_, var entry] when ownerValid && EntryId.IsValid(entry) => new DocsAddress(owner, entry),
            _ => throw new UsageException(
                $"'{text}' is not a valid address. Use <feature> or <feature>{EntryId.Separator}<entry>, and {FeatureId.RepoFileStem}{EntryId.Separator}<entry> for repo.yaml entries; ids are lowercase kebab-case."),
        };
    }
}
