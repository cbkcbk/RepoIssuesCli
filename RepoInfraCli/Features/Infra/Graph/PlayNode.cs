namespace RepoInfraCli.Features.Infra.Graph;

public sealed record PlayNode(
    NodeId Id,
    string Name,
    IReadOnlyList<NodeId> Hosts,
    IReadOnlyList<string> Roles,
    IReadOnlyList<NodeId> Apps,
    IReadOnlyList<RoleRun> Runs,
    SourceLocation? Location);

public sealed record RoleRun(NodeId Host, string Role, bool External, IReadOnlyList<TaskNode> Tasks)
{
    public const string AppPrefix = "app:";
}

public static class TaskKinds
{
    public const string Apt = "apt";
    public const string AptRepo = "apt_repo";
    public const string User = "user";
    public const string File = "file";
    public const string Ufw = "ufw";
    public const string Service = "service";
    public const string Handler = "handler";
    public const string Compose = "compose";
    public const string Shell = "shell";
}

public sealed record TaskNode(
    NodeId Id,
    NodeId Host,
    string Kind,
    string? Name,
    MapValue Options,
    SourceLocation? Location,
    NodeId? Notifies);
