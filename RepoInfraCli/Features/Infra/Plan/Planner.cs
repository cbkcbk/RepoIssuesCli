using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Plan;

public sealed record PlanOptions
{
    public required InfraWorkspace Workspace { get; init; }

    public PlanFilter Filter { get; init; } = PlanFilter.All;

    public bool AcceptNewHosts { get; init; }

    public InfraLog Log { get; init; } = InfraLog.Silent();

    public BecomePasswordProvider? BecomePassword { get; init; }

    public SecretLookup? Secrets { get; init; }

    public bool AllowDirty { get; init; }
}

public sealed class Planner(IHostConnector connector, ILiveValues? live = null, IResourcePlanner? resources = null, HostTaskRegistry? tasks = null, ContainerBuilds? builds = null)
{
    readonly ILiveValues values = live ?? NoLiveValues.Instance;
    readonly IResourcePlanner resourcePlanner = resources ?? NoResourcePlanner.Instance;
    readonly HostTaskRegistry modules = tasks ?? HostTaskRegistry.Default;
    readonly ContainerBuilds containerBuilds = builds ?? ContainerBuilds.Default;

    public const string UnresolvedNote = "from a repo.provider that could not be fetched";

    sealed record HostState(HostNode Host, string? Address, HostStatus Status, string? Note, IHostConnection? Connection);

    public async Task<PlanRun> PlanAsync(InfraGraph graph, PlanOptions options, CancellationToken cancellationToken = default)
    {
        options.Filter.Validate(graph);
        var buildPlans = await containerBuilds.PlanAsync(graph, options.Filter, options.Workspace, options.AllowDirty, cancellationToken);
        var secrets = options.Secrets ?? SecretLookup.Unavailable(options.Workspace);
        var live = new SelfHostedDatabases(new BuildImages(values, buildPlans), graph, secrets);
        var run = new PlanRun(graph, options.Workspace, live, secrets) { Options = options };
        try
        {
            var resourcePlan = options.Filter.HostsOnly ? ResourcePlan.Empty : await PlanResourcesAsync(run, options.Filter, cancellationToken);
            var resourceChanges = resourcePlan.Changes;
            var plays = graph.Plays.Where(options.Filter.IncludesPlay).ToList();
            var hosts = plays.SelectMany(play => play.Hosts).Distinct()
                .Select(id => graph.Host(id)).OfType<HostNode>()
                .Where(options.Filter.IncludesHost)
                .ToList();

            var states = new Dictionary<NodeId, HostState>();
            var problems = new List<PlanProblem>(resourcePlan.Problems);
            foreach (var host in hosts)
            {
                var state = await OpenAsync(host, options, cancellationToken);
                states[host.Id] = state;
                if (state.Connection is not null)
                    run.Connections[host.Id] = state.Connection;
                if (state.Status is HostStatus.Failed or HostStatus.External)
                    problems.Add(new PlanProblem(host.Name, state.Note ?? "failed"));
            }

            var playPlans = new List<PlayPlan>();
            foreach (var play in plays)
            {
                var included = play.Hosts.Where(states.ContainsKey).Select(id => states[id]).ToList();
                if (included.Count == 0)
                    continue;
                var hostPlans = await Task.WhenAll(included.Select(state => PlanHostAsync(play, state, run, cancellationToken)));
                playPlans.Add(new PlayPlan(play.Name, hostPlans));
                problems.AddRange(hostPlans.SelectMany(host => host.Tasks
                    .Where(task => task.Error is not null)
                    .Select(task => new PlanProblem(host.Host.Name, $"{play.Name} > {task.Label}: {task.Error}"))));
                problems.AddRange(hostPlans.SelectMany(host => host.Roles
                    .Where(role => role.External)
                    .Select(role => new PlanProblem(host.Host.Name, $"{play.Name} > {role.Name}: role {UnresolvedNote}"))));
            }

            problems.InsertRange(0, buildPlans.Where(build => build.Error is not null).Select(build => new PlanProblem(null, $"{build.Label}: {build.Error}")));
            run.Plan = new InfraPlan(resourceChanges, playPlans, problems, !options.Filter.HostsOnly)
            {
                Builds = buildPlans,
                Copies = CopySteps.Plan(graph, options.Filter),
            };
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    async Task<ResourcePlan> PlanResourcesAsync(PlanRun run, PlanFilter filter, CancellationToken cancellationToken) =>
        resourcePlanner is ILiveResourcePlanner livePlanner
            ? await livePlanner.PlanAsync(run, filter, cancellationToken)
            : new ResourcePlan(await resourcePlanner.PlanAsync(run.Graph, filter, cancellationToken), []);

    public async Task<IReadOnlyList<PlanProblem>> PlanUnknownHostsAsync(PlanRun run, int attempts, TimeSpan retryDelay, CancellationToken cancellationToken = default)
    {
        var options = run.Options ?? throw new InvalidOperationException("the run was not made by Planner.PlanAsync");
        var states = new Dictionary<NodeId, HostState>();
        var problems = new List<PlanProblem>();
        var plays = new List<PlayPlan>();
        foreach (var play in run.Plan.Plays)
        {
            var node = run.Graph.Play(play.Name)!;
            var hosts = new List<HostPlan>();
            foreach (var host in play.Hosts)
            {
                if (host.Status != HostStatus.AddressUnknown)
                {
                    hosts.Add(host);
                    continue;
                }

                if (!states.TryGetValue(host.Host.Id, out var state))
                {
                    state = await OpenAsync(host.Host, options, cancellationToken);
                    for (var attempt = 1; attempt < attempts && state.Status == HostStatus.Failed; attempt++)
                    {
                        await Task.Delay(retryDelay, cancellationToken);
                        state = await OpenAsync(host.Host, options, cancellationToken);
                    }

                    states[host.Host.Id] = state;
                    if (state.Connection is not null)
                        run.Connections[host.Host.Id] = state.Connection;
                    if (state.Status is HostStatus.Failed or HostStatus.AddressUnknown)
                        problems.Add(new PlanProblem(host.Host.Name, state.Note ?? "failed"));
                }

                var planned = await PlanHostAsync(node, state, run, cancellationToken);
                hosts.Add(planned);
                problems.AddRange(planned.Tasks.Where(task => task.Error is not null)
                    .Select(task => new PlanProblem(host.Host.Name, $"{play.Name} > {task.Label}: {task.Error}")));
            }

            plays.Add(play with { Hosts = hosts });
        }

        run.Plan = run.Plan with { Plays = plays, Problems = [.. run.Plan.Problems, .. problems] };
        return problems;
    }

    async Task<HostState> OpenAsync(HostNode host, PlanOptions options, CancellationToken cancellationToken)
    {
        if (host.External)
            return new HostState(host, null, HostStatus.External, UnresolvedNote, null);

        var address = host.Address switch
        {
            StringValue text => text.Value,
            OutputRef output => values.Resolve(output) is StringValue resolved ? resolved.Value : null,
            _ => null,
        };
        if (address is null)
            return new HostState(host, null, HostStatus.AddressUnknown, "address known after apply; skipped", null);

        try
        {
            var connection = await connector.ConnectAsync(host, new HostConnectOptions
            {
                Address = address,
                AcceptNewHosts = options.AcceptNewHosts,
                TrustOnFirstUse = host.Resource is { } resource && values.CreatedThisRun(resource),
                Log = options.Log,
                BecomePassword = options.BecomePassword,
            }, cancellationToken);
            return new HostState(host, address, HostStatus.Planned, null, connection);
        }
        catch (HostException exception)
        {
            options.Log.Error("host.failed", exception.Message, new Dictionary<string, object?> { ["host"] = host.Name });
            return new HostState(host, address, HostStatus.Failed, exception.Message, null);
        }
    }

    async Task<HostPlan> PlanHostAsync(PlayNode play, HostState state, PlanRun planRun, CancellationToken cancellationToken)
    {
        if (state.Connection is not { } connection)
            return new HostPlan(state.Host, state.Address, state.Status, state.Note, []);

        var roles = new List<RolePlan>();
        foreach (var run in play.Runs.Where(run => run.Host == state.Host.Id))
        {
            if (run.External)
            {
                roles.Add(new RolePlan(run.Role, true, []));
                continue;
            }

            var taskPlans = new List<TaskPlan>();
            foreach (var task in run.Tasks)
                taskPlans.Add(await ProbeAsync(new TaskContext(connection, task, planRun.Workspace, planRun.Live, planRun.Secrets), cancellationToken));
            roles.Add(new RolePlan(run.Role, false, taskPlans));
        }

        return new HostPlan(state.Host, state.Address, HostStatus.Planned, null, MarkNotifiedHandlers(roles));
    }

    async Task<TaskPlan> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var task = context.Task;
        var module = modules.For(task.Kind);
        try
        {
            var probe = await module.ProbeAsync(context, cancellationToken);
            return new TaskPlan(task, module.Label(task), probe);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TaskPlan(task, module.Label(task), TaskProbe.None, exception.Message);
        }
    }

    static List<RolePlan> MarkNotifiedHandlers(List<RolePlan> roles)
    {
        var notified = roles.SelectMany(role => role.Tasks)
            .Where(task => task.Task.Notifies is not null && task.Probe.HasDrift)
            .Select(task => task.Task.Notifies!.Value)
            .ToHashSet();
        return
        [
            .. roles.Select(role => role with
            {
                Tasks =
                [
                    .. role.Tasks.Select(task => task.Task.Kind == TaskKinds.Handler && notified.Contains(task.Task.Id)
                        ? task with { Probe = new TaskProbe([HandlerTask.Notified(task.Task)]) }
                        : task)
                ],
            })
        ];
    }
}