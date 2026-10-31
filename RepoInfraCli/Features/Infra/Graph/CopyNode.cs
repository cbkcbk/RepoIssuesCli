namespace RepoInfraCli.Features.Infra.Graph;

public sealed record CopyNode(NodeId Id, NodeId From, NodeId To, string? Scrub, NodeId? Migrate, SourceLocation? Location)
{
    public const string Kind = "postgres.copy";
}
