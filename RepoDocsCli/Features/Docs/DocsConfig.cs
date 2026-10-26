namespace RepoDocsCli.Features.Docs;

public sealed record DocsConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public Dictionary<string, KindConfig> Kinds { get; init; } = [];

    public bool Declares(string? kind) => kind is not null && Kinds.ContainsKey(kind);

    public static DocsConfig Default() => new()
    {
        Kinds = new Dictionary<string, KindConfig>
        {
            ["rule"] = new() { Render = RenderMode.Index, Push = PushPolicy.OnEdit },
            ["fact"] = new() { Push = PushPolicy.OnRead, Prune = new PrunePolicy { UnreadDays = 180 } },
            ["gotcha"] = new() { Push = PushPolicy.OnEdit, Prune = new PrunePolicy { UnreadDays = 365 } },
            ["howto"] = new() { Render = RenderMode.Index, Prune = new PrunePolicy { UnreadDays = 180 } },
            ["decision"] = new(),
            ["plan"] = new() { Push = PushPolicy.WhenFeatureActive },
        },
    };
}
