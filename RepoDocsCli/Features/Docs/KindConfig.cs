using System.Text.Json.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed record KindConfig
{
    public RenderMode Render { get; init; } = RenderMode.Never;

    public PushPolicy Push { get; init; } = PushPolicy.Never;

    public PrunePolicy? Prune { get; init; }
}

public sealed record PrunePolicy
{
    public int? UnreadDays { get; init; }
}

public enum RenderMode
{
    [JsonStringEnumMemberName("always")]
    Always,

    [JsonStringEnumMemberName("index")]
    Index,

    [JsonStringEnumMemberName("never")]
    Never,
}

public enum PushPolicy
{
    [JsonStringEnumMemberName("on-edit")]
    OnEdit,

    [JsonStringEnumMemberName("on-read")]
    OnRead,

    [JsonStringEnumMemberName("when-feature-active")]
    WhenFeatureActive,

    [JsonStringEnumMemberName("never")]
    Never,
}
