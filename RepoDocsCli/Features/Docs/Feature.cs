using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed record Feature
{
    [YamlMember(Alias = "feature")]
    [JsonPropertyName("feature")]
    public required string Id { get; init; }

    public FeatureStatus Status { get; init; } = FeatureStatus.Proposed;

    public string? Summary { get; init; }

    public WhenHints When { get; init; } = new();

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Updated { get; init; }

    public List<FeatureEntry> Entries { get; init; } = [];
}

public enum FeatureStatus
{
    [JsonStringEnumMemberName("proposed")]
    Proposed,

    [JsonStringEnumMemberName("planned")]
    Planned,

    [JsonStringEnumMemberName("in-progress")]
    InProgress,

    [JsonStringEnumMemberName("shipped")]
    Shipped,

    [JsonStringEnumMemberName("deprecated")]
    Deprecated,
}
