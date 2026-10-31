using NuLua;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed record PlayDraft(
    NodeId Id,
    string Name,
    IReadOnlyList<(NodeId Id, LuaValue Table)> Hosts,
    IReadOnlyList<LuaValue> Roles,
    IReadOnlyList<NodeId> Apps,
    SourceLocation? Location);

public sealed record AppDraft(NodeId Id, string Name, BuildNode Build, LuaFunction? Deploy, SourceLocation? Location);

public sealed record DatabaseInfo(NodeId Id, string Name, string User, string? Version, LuaValue Password, LuaValue Network, string? Data)
{
    public bool SelfHosted => Data is not null;
}

public sealed class RunRecorder(NodeId host, string? role)
{
    public NodeId Host => host;

    public string? Role { get; set; } = role;

    public List<TaskNode> Tasks { get; } = [];
}

public sealed class GraphRecorder
{
    readonly HashSet<string> secretPaths = new(StringComparer.Ordinal);
    int taskSequence;

    public List<ProjectNode> Projects { get; } = [];

    public List<ProviderNode> Providers { get; } = [];

    public List<ResourceNode> Resources { get; } = [];

    public List<HostNode> Hosts { get; } = [];

    public List<PlayDraft> Plays { get; } = [];

    public List<AppDraft> Apps { get; } = [];

    public List<ComponentNode> Components { get; } = [];

    public List<BuildNode> Builds { get; } = [];

    public List<SecretRef> Secrets { get; } = [];

    public List<CopyNode> Copies { get; } = [];

    public Dictionary<NodeId, DatabaseInfo> Databases { get; } = [];

    public Dictionary<NodeId, IReadOnlyList<RoleRun>> Runs { get; } = [];

    public ExportsNode? Exports { get; set; }

    public RunRecorder? CurrentRun { get; set; }

    public void AddSecret(string path)
    {
        if (secretPaths.Add(path))
            Secrets.Add(new SecretRef(path));
    }

    public NodeId NextTaskId(NodeId host, string kind) => new($"{host.Value}/{kind}#{++taskSequence}");

    public NodeId NextProviderId(string kind)
    {
        var count = Providers.Count(provider => provider.Kind == kind && provider.Reference is null);
        return new NodeId(count == 0 ? kind : $"{kind}#{count + 1}");
    }

    public string UniqueSuffix(string name, Func<string, bool> taken)
    {
        if (!taken(name))
            return name;
        for (var index = 2; ; index++)
        {
            var candidate = $"{name}#{index}";
            if (!taken(candidate))
                return candidate;
        }
    }

    public void AddComponent(ComponentNode component)
    {
        if (Components.All(existing => existing.Id != component.Id))
            Components.Add(component);
    }

    public InfraGraph? Build(string? env)
    {
        if (Projects.FirstOrDefault() is not { } project)
            return null;

        var exportedResources = ExportedResources();
        var resources = Resources.Select(resource => exportedResources.Contains(resource.Id) ? resource with { Exported = true } : resource).ToList();
        var plays = Plays.Select(play => new PlayNode(
            play.Id,
            play.Name,
            [.. play.Hosts.Select(host => host.Id)],
            [.. Runs.GetValueOrDefault(play.Id, []).Select(run => run.Role).Distinct()],
            play.Apps,
            Runs.GetValueOrDefault(play.Id, []),
            play.Location)).ToList();
        var apps = Apps.Select(app => new AppNode(app.Id, app.Name, app.Build, app.Location)).ToList();

        return new InfraGraph(project, env, [.. Providers], resources, [.. Hosts], plays, apps, [.. Components], [.. Secrets], Exports) { Copies = [.. Copies] };
    }

    HashSet<NodeId> ExportedResources()
    {
        var exported = new HashSet<NodeId>();
        if (Exports is null)
            return exported;

        foreach (var node in Exports.Nodes)
        {
            if (Hosts.FirstOrDefault(host => host.Id == node)?.Resource is { } hostResource)
                exported.Add(hostResource);
            if (Resources.FirstOrDefault(resource => resource.Id == node) is { } resource)
            {
                exported.Add(resource.Id);
                if (resource.Parent is { } parent)
                    exported.Add(parent);
            }
        }
        return exported;
    }
}
