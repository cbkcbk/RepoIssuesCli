using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed record Feature
{
    [YamlMember(Alias = "feature")]
    [JsonPropertyName("feature")]
    public required string Id { get; init; }

    public FeatureStatus Status { get; init; } = FeatureStatus.Proposed;

    public string? Summary { get; init; }

    public FeatureWhen When { get; init; } = new();

    public List<FeatureEntry> Entries { get; init; } = [];
}

public sealed record FeatureWhen
{
    public List<string> Paths { get; init; } = [];
}

public sealed record FeatureEntry
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Summary { get; init; }

    [YamlMember(ScalarStyle = ScalarStyle.Literal)]
    public string? Body { get; init; }
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
