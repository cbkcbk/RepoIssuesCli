namespace RepoInfraCli.Features.Infra.Graph;

public sealed record ProjectNode(string Name, bool Prune, SourceLocation? Location);

public sealed record ProviderNode(NodeId Id, string Kind, MapValue Options, SourceLocation? Location)
{
    public Marker? Reference { get; init; }
}

public sealed record ComponentNode(NodeId Id, string Module, string Kind, string Name, MapValue Options, bool External, SourceLocation? Location);

public sealed record ExportsNode(
    IReadOnlyDictionary<string, NodeId> Hosts,
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, NodeId> Networks,
    IReadOnlyDictionary<string, NodeId> Databases,
    SourceLocation? Location)
{
    public IEnumerable<NodeId> Nodes => Hosts.Values.Concat(Networks.Values).Concat(Databases.Values);
}
