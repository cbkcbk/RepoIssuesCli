namespace RepoDocsCli.Features.Docs;

public sealed record DocsAddress(string Feature, string? Entry = null)
{
    public bool IsEntry => Entry is not null;

    public bool IsRepo => Feature == FeatureId.RepoFileStem;

    public static DocsAddress Parse(string text)
    {
        var parts = text.Trim().Split(EntryId.Separator);
        if (parts.Length > 2 || parts.Any(string.IsNullOrEmpty))
            throw new UsageException($"'{text}' is not a feature id, feature/entry or repo/entry.");

        return new DocsAddress(parts[0], parts.ElementAtOrDefault(1));
    }

    public override string ToString() => Entry is null ? Feature : EntryId.Address(Feature, Entry);
}
