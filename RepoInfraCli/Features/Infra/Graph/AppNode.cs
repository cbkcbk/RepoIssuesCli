namespace RepoInfraCli.Features.Infra.Graph;

public sealed record AppNode(NodeId Id, string Name, BuildNode Build, SourceLocation? Location);

public sealed record BuildNode(NodeId Id, string Kind, MapValue Options, SourceLocation? Location);
