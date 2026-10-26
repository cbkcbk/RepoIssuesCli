using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed record FeatureEntry
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Summary { get; init; }

    public WhenHints? When { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? Updated { get; init; }

    public DateOnly? Read { get; init; }

    [YamlMember(ScalarStyle = ScalarStyle.Literal)]
    public string? Body { get; init; }
}
