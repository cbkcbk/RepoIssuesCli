namespace RepoInfraCli.Features.Infra.Graph;

public sealed record HostNode(
    NodeId Id,
    string Name,
    InfraValue Address,
    string User,
    int? Port,
    bool Become,
    SecretRef? BecomePassword,
    NodeId? Resource,
    bool External,
    NodeId? Provider,
    SourceLocation? Location);