using YamlDotNet.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed record PlanStep
{
    public const string EntryKind = "plan";

    public required string Title { get; init; }

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<int> Issues { get; init; } = [];
}
