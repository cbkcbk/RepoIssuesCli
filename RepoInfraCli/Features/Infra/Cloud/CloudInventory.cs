using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed record NodeMatch(ResourceNode Node, ResourceHandler Handler, ProviderSession Session, LiveResource? Live, LiveResource? Unowned);

public sealed record StrayResource(ResourceHandler Handler, ProviderSession Session, LiveResource Live);

public sealed class CloudInventory
{
    public required Marker Marker { get; init; }

    public required IReadOnlyList<ResourceNode> Order { get; init; }

    public Dictionary<NodeId, NodeMatch> Matches { get; } = [];

    public List<StrayResource> Strays { get; } = [];

    public List<PlanProblem> Problems { get; } = [];

    public static async Task<CloudInventory> ScanAsync(CloudServices cloud, PlanRun run, InfraLog log, bool allKinds, CancellationToken cancellationToken)
    {
        var graph = run.Graph;
        var managed = graph.Resources.Where(resource => cloud.Handles(resource.Provider)).ToList();
        var inventory = new CloudInventory { Marker = graph.Marker, Order = TopologicalOrder(managed) };
        cloud.State.Reset();

        var sessions = new Dictionary<NodeId, ProviderSession>();
        var pools = new Dictionary<(NodeId Provider, string Kind), IReadOnlyList<LiveResource>>();
        foreach (var provider in graph.Providers.Where(provider => cloud.Handles(provider.Kind)))
        {
            var declared = managed.Where(resource => resource.ProviderId == provider.Id).ToList();
            var listsAll = allKinds && provider.Reference is null;
            if (declared.Count == 0 && !listsAll)
                continue;

            var session = new ProviderSession(provider, cloud, run.Secrets, log);
            try
            {
                session.Token();
            }
            catch (Exception exception) when (exception is SecretUnavailableException or ResourcePlanException)
            {
                inventory.Problem($"{provider.Kind}.provider: {exception.Message}");
                continue;
            }

            sessions[provider.Id] = session;
            session.SpacesRegions.UnionWith(declared.Where(resource => resource.Kind == SpacesBucketHandler.KindName)
                .Select(resource => resource.Options.GetString("region")).OfType<string>());
            if (listsAll)
                session.SpacesRegions.UnionWith(SpacesBucketHandler.Regions);

            foreach (var handler in cloud.Handlers.Where(handler => handler.Provider == provider.Kind && handler.ParentKind is null))
            {
                var used = declared.Any(resource => resource.Kind == handler.Kind);
                if (!used && (!listsAll || handler.LookupOnly))
                    continue;
                try
                {
                    pools[(provider.Id, handler.Kind)] = await handler.ListAsync(session, inventory.Marker, cancellationToken);
                }
                catch (SecretUnavailableException exception) when (!used)
                {
                    log.Debug("cloud.skip", $"{provider.Kind} {handler.Kind} not listed: {exception.Message}");
                }
                catch (Exception exception) when (exception is SecretUnavailableException or CloudApiException or HttpRequestException or ResourcePlanException)
                {
                    inventory.Problem($"{provider.Kind} {handler.Kind}: {exception.Message}");
                }
            }
        }

        var claimed = new List<LiveResource>();
        foreach (var node in inventory.Order)
        {
            if (!sessions.TryGetValue(node.ProviderId, out var session))
                continue;
            if (cloud.Handler(node.Provider, node.Kind) is not { } handler)
            {
                inventory.Problem($"{node.Provider} {node.Kind} \"{node.Name}\" is not supported yet");
                continue;
            }

            var parent = node.Parent is { } parentId ? inventory.Matches.GetValueOrDefault(parentId) : null;
            if (node.Parent is not null && parent is null)
                continue;
            var pool = parent is not null
                ? parent.Live is { } parentLive ? handler.ChildrenOf(parentLive) : []
                : pools.GetValueOrDefault((node.ProviderId, node.Kind));
            if (pool is null)
                continue;

            try
            {
                await MatchAsync(inventory, node, handler, session, pool, parent?.Live, claimed, cloud.State, cancellationToken);
            }
            catch (Exception exception) when (exception is ResourcePlanException or CloudApiException or HttpRequestException)
            {
                inventory.Problem($"{Describe(node, handler)}: {exception.Message}");
            }
        }

        bool Stray(LiveResource live) => live.Mark is { } mark && mark.BelongsTo(inventory.Marker) && !claimed.Any(live.Is);
        foreach (var ((providerId, kind), pool) in pools.Where(pool => sessions[pool.Key.Provider].Provider.Reference is null))
        {
            var handler = cloud.Handler(sessions[providerId].Provider.Kind, kind)!;
            if (handler.Prunable)
                inventory.Strays.AddRange(pool.Where(Stray).Select(live => new StrayResource(handler, sessions[providerId], live)));
        }

        foreach (var match in inventory.Matches.Values.Where(match => match.Live is not null && match.Node.Reference is null).ToList())
        foreach (var handler in cloud.Handlers.Where(handler => handler.ParentKind == match.Handler.Kind && handler.Prunable))
            inventory.Strays.AddRange(handler.ChildrenOf(match.Live!).Where(Stray).Select(live => new StrayResource(handler, match.Session, live)));

        return inventory;
    }

    static async Task MatchAsync(
        CloudInventory inventory, ResourceNode node, ResourceHandler handler, ProviderSession session, IReadOnlyList<LiveResource> pool, LiveResource? parent,
        List<LiveResource> claimed, CloudState state, CancellationToken cancellationToken)
    {
        if (node.IsLookup)
        {
            var found = node.Reference is { } owner ? handler.FindOwned(node, pool, owner) : handler.FindExisting(node, pool);
            if (found is null)
            {
                inventory.Problem(node.Reference is { } reference
                    ? $"{Describe(node, handler)} with the marker {reference.Label} was not found; it comes from repo.provider, so apply it in the repo that declares it first"
                    : $"{Describe(node, handler)} was not found; existing resources are looked up, never created");
                return;
            }

            claimed.Add(found);
            var bound = await handler.BindAsync(session, node, found, parent, cancellationToken);
            state.Found(node.Id, bound);
            inventory.Matches[node.Id] = new NodeMatch(node, handler, session, bound, null);
            return;
        }

        var marker = inventory.Marker;
        var owned = handler.FindOwned(node, pool, marker);
        var unowned = owned is null ? handler.FindUnowned(node, pool, marker) : null;
        if (owned is null && unowned is null && handler.UniqueNames
            && pool.FirstOrDefault(live => live.Name == handler.LiveName(node, marker)) is { Mark: { } other })
            inventory.Problem($"{Describe(node, handler)}: the name {handler.LiveName(node, marker)} is already taken by {other.Marker.Label}");

        if (owned is not null)
        {
            claimed.Add(owned);
            owned = await handler.BindAsync(session, node, owned, parent, cancellationToken);
            state.Found(node.Id, owned);
        }

        if (unowned is not null)
            claimed.Add(unowned);
        inventory.Matches[node.Id] = new NodeMatch(node, handler, session, owned, unowned);
    }

    public void Problem(string message) => Problems.Add(new PlanProblem(null, message));

    public static string Describe(ResourceNode node, ResourceHandler handler) => $"{node.Provider} {node.Kind} \"{handler.Display(node)}\"";

    public static IReadOnlyList<ResourceNode> TopologicalOrder(IReadOnlyList<ResourceNode> nodes)
    {
        var ids = nodes.Select(node => node.Id).ToHashSet();
        var done = new HashSet<NodeId>();
        var remaining = nodes.ToList();
        var order = new List<ResourceNode>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(node => node.DependsOn.All(dependency => !ids.Contains(dependency) || done.Contains(dependency))) ?? remaining[0];
            order.Add(next);
            done.Add(next.Id);
            remaining.Remove(next);
        }

        return order;
    }
}