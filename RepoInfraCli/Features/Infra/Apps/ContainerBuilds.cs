using System.Text.RegularExpressions;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed partial class ContainerBuilds(IBuildTools tools)
{
    public static ContainerBuilds Default { get; } = new(DotnetBuildTools.Default);

    sealed record TreeState(string? DirtyHash, string? Error);

    public static IReadOnlyList<(BuildNode Build, IReadOnlyList<string> Apps)> Included(InfraGraph graph, PlanFilter filter) =>
    [
        .. graph.Plays
            .Where(filter.IncludesPlay)
            .Where(play => play.Hosts.Select(graph.Host).OfType<HostNode>().Any(filter.IncludesHost))
            .SelectMany(play => play.Apps)
            .Distinct()
            .Select(id => graph.Apps.FirstOrDefault(app => app.Id == id))
            .OfType<AppNode>()
            .GroupBy(app => app.Build.Id)
            .Select(group => (group.First().Build, (IReadOnlyList<string>)[.. group.Select(app => app.Name)])),
    ];

    public async Task<IReadOnlyList<BuildPlan>> PlanAsync(InfraGraph graph, PlanFilter filter, InfraWorkspace workspace, bool allowDirty, CancellationToken cancellationToken)
    {
        var builds = Included(graph, filter);
        if (builds.Count == 0)
            return [];

        var tree = await TreeAsync(workspace, cancellationToken);
        var plans = new List<BuildPlan>();
        foreach (var (build, apps) in builds)
            plans.Add(await PlanAsync(build, apps, workspace, tree, allowDirty, cancellationToken));
        return plans;
    }

    public Task ApplyAsync(BuildPlan plan, InfraWorkspace workspace, CancellationToken cancellationToken) =>
        tools.PublishAsync(plan.Publish ?? throw new InvalidOperationException($"{plan.Label} has nothing to build"), workspace.Root, cancellationToken);

    async Task<TreeState> TreeAsync(InfraWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            return new TreeState(await tools.DirtyHashAsync(workspace.Root, cancellationToken), null);
        }
        catch (BuildToolException exception)
        {
            return new TreeState(null, exception.Message);
        }
    }

    async Task<BuildPlan> PlanAsync(BuildNode build, IReadOnlyList<string> apps, InfraWorkspace workspace, TreeState tree, bool allowDirty, CancellationToken cancellationToken)
    {
        var label = build.Id.Value.Replace(':', ' ');
        BuildPlan Failed(string error) => new(build, label, apps, null, [], Error: error);

        if (build.Kind != DotnetModule.ContainerKind)
            return Failed($"no builder handles {build.Kind}");
        var project = build.Options.GetString("project") ?? "";
        var projectPath = ContainerSettings.ProjectPath(workspace, build);
        if (!File.Exists(projectPath))
            return Failed($"project {project} does not exist ({projectPath})");

        ContainerProperties properties;
        try
        {
            properties = await tools.ReadPropertiesAsync(projectPath, cancellationToken);
        }
        catch (BuildToolException exception)
        {
            return Failed(exception.Message);
        }

        var resolution = ContainerSettings.Resolve(build, properties);
        if (resolution.Target is not { } target)
            return Failed(string.Join("; ", resolution.Conflicts));
        if (tree.Error is not null)
            return Failed(tree.Error);

        var tag = build.Options.GetString("tag") ?? "";
        if (tag.Length == 0)
            return Failed("the tag is empty; infra.git.sha is empty outside a git repository with commits");
        if (tree.DirtyHash is { } dirtyHash)
        {
            if (!allowDirty)
                return Failed($"the working tree has uncommitted changes; commit them, or pass --allow-dirty to build {tag}-dirty-<hash of the changes>");
            tag = $"{tag}-dirty-{dirtyHash}";
        }

        if (!ValidTag().IsMatch(tag))
            return Failed($"\"{tag}\" is not a valid image tag (letters, digits, '_', '.', '-'; at most 128)");

        var publish = new ContainerPublish(
            projectPath,
            build.Options.GetString("configuration") ?? ContainerSettings.DefaultConfiguration,
            build.Options.GetString("runtime") ?? ContainerSettings.DefaultRuntime,
            target.Registry,
            target.Repository,
            tag);
        var check = await tools.TagExistsAsync(publish.Registry, publish.Repository, publish.Tag, cancellationToken);
        if (check.Exists == true)
            return new BuildPlan(build, label, apps, publish.Image, [], Note: $"{publish.Image} is already in the registry");

        var details = new List<string>();
        if (target.BaseImage is { } baseImage)
            details.Add($"from {baseImage}");
        if (check.Problem is { } problem)
            details.Add($"could not check the registry: {problem}");
        var text = $"build and push {publish.Image}" + (details.Count > 0 ? $" ({string.Join("; ", details)})" : "");
        return new BuildPlan(build, label, apps, publish.Image, [Change.Add(text)], Publish: publish);
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex ValidTag();
}