namespace RepoInfraCli.Features.Infra.Graph;

public sealed record InfraGraph(
    ProjectNode Project,
    string? Env,
    IReadOnlyList<ProviderNode> Providers,
    IReadOnlyList<ResourceNode> Resources,
    IReadOnlyList<HostNode> Hosts,
    IReadOnlyList<PlayNode> Plays,
    IReadOnlyList<AppNode> Apps,
    IReadOnlyList<ComponentNode> Components,
    IReadOnlyList<SecretRef> Secrets,
    ExportsNode? Exports)
{
    public Marker Marker => new(Project.Name, Env);

    public IEnumerable<TaskNode> Tasks => Plays.SelectMany(play => play.Runs).SelectMany(run => run.Tasks);

    public ResourceNode? Resource(NodeId id) => Resources.FirstOrDefault(node => node.Id == id);

    public HostNode? Host(NodeId id) => Hosts.FirstOrDefault(node => node.Id == id);

    public ProviderNode? Provider(NodeId id) => Providers.FirstOrDefault(node => node.Id == id);

    public ComponentNode? Component(NodeId id) => Components.FirstOrDefault(node => node.Id == id);

    public PlayNode? Play(string name) => Plays.FirstOrDefault(play => play.Name == name);

    public AppNode? App(string name) => Apps.FirstOrDefault(app => app.Name == name);
}
