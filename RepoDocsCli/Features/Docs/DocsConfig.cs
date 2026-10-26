namespace RepoDocsCli.Features.Docs;

public sealed record DocsConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public Dictionary<string, KindConfig> Kinds { get; init; } = [];

    public static DocsConfig Default() => new()
    {
        Kinds = new[] { "rule", "fact", "gotcha", "howto", "decision", "plan" }.ToDictionary(kind => kind, _ => new KindConfig()),
    };
}

public sealed record KindConfig;
