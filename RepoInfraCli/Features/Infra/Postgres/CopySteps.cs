using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record CopyPlan(CopyNode Copy, string Label, string From, string To, string? Migrate, bool Protected, IReadOnlyList<Change> Changes);

public sealed class CopySteps(DatabaseCopier copier)
{
    public static CopySteps Default { get; } = new(DatabaseCopier.Default);

    public static IReadOnlyList<CopyPlan> Plan(InfraGraph graph, PlanFilter filter) =>
        filter.ResourcesOnly || filter.HostsOnly || filter.Plays.Count > 0 || filter.Hosts.Count > 0
            ? []
            : [.. graph.Copies.Select(copy => Plan(graph, copy))];

    static CopyPlan Plan(InfraGraph graph, CopyNode copy)
    {
        var from = DeclaredDatabases.Display(graph, copy.From);
        var to = DeclaredDatabases.Display(graph, copy.To);
        var isProtected = CopyRules.IsProtected(graph, copy.To);
        var migrate = copy.Migrate is { } app ? graph.Apps.FirstOrDefault(node => node.Id == app)?.Name ?? app.Value : null;
        var details = new List<string>();
        if (copy.Scrub is { } scrub)
            details.Add($"scrub {scrub}");
        if (migrate is not null)
            details.Add($"migrate with {migrate}");
        if (isProtected)
            details.Add("target is protected");
        var text = $"replace {to} with a copy of {from}" + (details.Count > 0 ? $" ({string.Join("; ", details)})" : "");
        return new CopyPlan(copy, $"postgres.copy {from} -> {to}", from, to, migrate, isProtected, [Change.Run(text)]);
    }

    public async Task ApplyAsync(CopyPlan plan, PlanRun run, InfraLog log, CancellationToken cancellationToken)
    {
        using var endpoints = new DatabaseEndpoints(run.Graph, run.Live, run.Secrets, (host, _) =>
            run.Connections.TryGetValue(host, out var connection)
                ? Task.FromResult(connection)
                : throw new DatabaseCopyException($"{host} is not connected in this run, so its postgres.server can't be reached"), log);
        var source = await endpoints.ResolveAsync(plan.Copy.From, cancellationToken);
        var target = await endpoints.ResolveAsync(plan.Copy.To, cancellationToken);
        var request = new CopyRequest(source, target)
        {
            DeclaredMajor = DeclaredDatabases.Of(run.Graph, plan.Copy.From)?.Major,
            Scrub = plan.Copy.Scrub is { } scrub ? Path.GetFullPath(scrub, run.Workspace.Folder) : null,
            MigrationsProject = plan.Copy.Migrate is { } app ? MigrationsProject(run.Graph, run.Workspace, app) : null,
        };
        using var interrupt = new CtrlC(cancellationToken);
        await copier.CopyAsync(request, log, null, interrupt.Token);
    }

    public static string MigrationsProject(InfraGraph graph, InfraWorkspace workspace, NodeId app) =>
        graph.Apps.FirstOrDefault(node => node.Id == app) is { } found
            ? ContainerSettings.ProjectPath(workspace, found.Build)
            : throw new DatabaseCopyException($"{app} is not an infra.app declared in this repo");

    public static IReadOnlyList<AppNode> AppsUsing(InfraGraph graph, NodeId database) =>
    [
        .. graph.Plays.SelectMany(play => play.Runs)
            .Where(run => run.Role.StartsWith(RoleRun.AppPrefix, StringComparison.Ordinal))
            .Where(run => run.Tasks.Any(task => task.Options.References().Contains(database)))
            .Select(run => graph.App(run.Role[RoleRun.AppPrefix.Length..]))
            .OfType<AppNode>()
            .DistinctBy(app => app.Id),
    ];

    public static AppNode MigrationsApp(InfraGraph graph, NodeId source, NodeId? target)
    {
        var step = graph.Copies.Where(copy => copy.From == source && copy.To == target && copy.Migrate is not null).Select(copy => copy.Migrate!.Value).Distinct().ToList();
        if (step is [var named])
            return graph.Apps.FirstOrDefault(app => app.Id == named) ?? throw new DatabaseCopyException($"{named} is not an infra.app declared in this repo");

        var database = target ?? source;
        var apps = AppsUsing(graph, database);
        var display = DeclaredDatabases.Display(graph, database);
        return apps switch
        {
            [var single] => single,
            [] => throw new DatabaseCopyException($"--migrate needs an app to run migrations from, but no app deploy uses \"{display}\" and no postgres.copy step names one"),
            _ => throw new DatabaseCopyException(
                $"--migrate is ambiguous: {string.Join(", ", apps.Select(app => app.Name))} all use \"{display}\"; declare postgres.copy(...) {{ migrate = app }} to pick one"),
        };
    }
}
