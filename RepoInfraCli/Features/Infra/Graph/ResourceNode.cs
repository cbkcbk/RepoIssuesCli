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
    public Marker? Reference { get; init; }

    public bool UsesNamePrefix => Marker.UsesNamePrefix(Kind);

    public bool IsLookup => Existing || Reference is not null;
}
