using RepoCli.Core.Features.Anchors;
using YamlDotNet.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed record WhenHints
{
    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Paths { get; init; } = [];

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<Anchor> Anchors { get; init; } = [];

    [YamlMember(DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
    public List<string> Keywords { get; init; } = [];

    public bool HasHints() => Paths.Count > 0 || Anchors.Count > 0 || Keywords.Count > 0;
}
