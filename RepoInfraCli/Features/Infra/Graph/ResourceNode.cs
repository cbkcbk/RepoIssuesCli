namespace RepoInfraCli.Features.Infra.Graph;

public sealed record ResourceNode(
    NodeId Id,
    string Provider,
    NodeId ProviderId,
    string Kind,
    string Name,
    NodeId? Parent,
    MapValue Options,
    bool Protect,
    bool Exported,
    bool Existing,
    SourceLocation? Location,
    IReadOnlyList<NodeId> DependsOn)
{
    public bool UsesNamePrefix => Marker.UsesNamePrefix(Kind);
}
