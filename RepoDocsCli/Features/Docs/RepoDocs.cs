namespace RepoDocsCli.Features.Docs;

public sealed record RepoDocs
{
    public string? Summary { get; init; }

    public List<FeatureEntry> Entries { get; init; } = [];
}
