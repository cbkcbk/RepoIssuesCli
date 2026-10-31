using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Cloud;

public enum ResourceAction
{
    Create,
    Update,
    Replace,
    Delete,
}

public sealed record ResourceDiff(
    ResourceAction Action,
    ResourceHandler Handler,
    ProviderSession Session,
    ResourceNode? Node,
    LiveResource? Live,
    IReadOnlySet<string> Changed)
{
    public string Name => Node is { } node ? Handler.Display(node) : Handler.Display(Live!);

    public bool Destroys => Action is ResourceAction.Delete or ResourceAction.Replace;
}

public sealed class CloudPlanner(CloudServices cloud) : IResourcePlanner, ILiveResourcePlanner
{
    public async Task<IReadOnlyList<ResourceChange>> PlanAsync(InfraGraph graph, PlanFilter filter, CancellationToken cancellationToken)
    {
        var workspace = new InfraWorkspace(Environment.CurrentDirectory);
        await using var run = new PlanRun(graph, workspace, cloud.Live, new SecretLookup(new EnvironmentSecretResolver(), workspace, graph.Env, new SecretRedactor()));
        return (await PlanAsync(run, filter, cancellationToken)).Changes;
    }

    public async Task<ResourcePlan> PlanAsync(PlanRun run, PlanFilter filter, CancellationToken cancellationToken)
    {
        var log = run.Options?.Log ?? InfraLog.Silent();
        var graph = run.Graph;
        var inventory = await cloud.ScanAsync(run, log, graph.Project.Prune, cancellationToken);
        var plan = new Builder(cloud.State, inventory.Marker, [.. inventory.Problems]);

        foreach (var node in inventory.Order)
        {
            if (!inventory.Matches.TryGetValue(node.Id, out var match))
            {
                cloud.State.Pend(node.Id);
                continue;
            }
            if (node.Existing)
                continue;
            if (match.Unowned is { } unowned)
            {
                plan.Problem($"{CloudInventory.Describe(node, match.Handler)} already exists ({unowned.Name}, id {unowned.Id}) without an infra marker; run 'infra adopt {node.Name}' to take it over");
                cloud.State.Pend(node.Id);
                continue;
            }
            plan.Diff(match);
        }

        if (graph.Project.Prune)
        {
            foreach (var stray in inventory.Strays.OrderBy(stray => stray.Handler.DestroyRank))
            {
                if (stray.Live.Mark is { Keeps: true } mark)
                {
                    log.Info("cloud.prune.kept", $"{stray.Session.Provider.Kind} {stray.Handler.Kind} {stray.Handler.Display(stray.Live)} is no longer in {InfraWorkspace.EntryFileName} but is {mark.KeepReason}; kept");
                    continue;
                }
                plan.Add(new ResourceDiff(ResourceAction.Delete, stray.Handler, stray.Session, null, stray.Live, new HashSet<string>()),
                    [Change.Remove($"delete {stray.Live.Id} (no longer in {InfraWorkspace.EntryFileName})")]);
            }
        }

        cloud.State.Diffs = plan.Diffs;
        return new ResourcePlan(plan.Changes, plan.Problems);
    }

    sealed class Builder(CloudState state, Marker marker, List<PlanProblem> problems)
    {
        readonly CloudValues values = new(state);

        public List<ResourceDiff> Diffs { get; } = [];

        public List<ResourceChange> Changes { get; } = [];

        public List<PlanProblem> Problems => problems;

        public void Problem(string message) => problems.Add(new PlanProblem(null, message));

        public void Add(ResourceDiff diff, IReadOnlyList<Change> changes)
        {
            Diffs.Add(diff);
            Changes.Add(new ResourceChange(diff.Session.Provider.Kind, diff.Handler.Kind, diff.Name, changes));
        }

        public void Diff(NodeMatch match)
        {
            var (node, handler, session) = (match.Node, match.Handler, match.Session);
            IReadOnlyDictionary<string, string?> desired;
            try
            {
                desired = handler.Desired(node, values);
            }
            catch (ResourcePlanException exception)
            {
                Problem($"{CloudInventory.Describe(node, handler)}: {exception.Message}");
                state.Pend(node.Id);
                return;
            }

            if (match.Live is not { } live)
            {
                state.Pend(node.Id);
                Add(new ResourceDiff(ResourceAction.Create, handler, session, node, null, new HashSet<string>()), [Change.Add("create" + Describe(handler, desired))]);
                return;
            }

            var changed = new HashSet<string>(StringComparer.Ordinal);
            var replaced = new List<string>();
            var updated = new List<string>();
            var notes = new List<Change>();
            foreach (var field in handler.Fields)
            {
                if (!desired.TryGetValue(field.Name, out var wanted) || !live.Fields.TryGetValue(field.Name, out var current) || wanted == current)
                    continue;
                var text = $"{field.Name} {Show(field, current)} -> {Show(field, wanted)}";
                switch (field.Mode)
                {
                    case FieldMode.Replace:
                        replaced.Add(text);
                        changed.Add(field.Name);
                        break;
                    case FieldMode.Update:
                        updated.Add(text);
                        changed.Add(field.Name);
                        break;
                    default:
                        notes.Add(Change.Unsupported($"{text} is create-only; not changed (recreate the {handler.Kind} to change it)"));
                        break;
                }
            }

            var mark = OwnerMark.For(node, marker);
            if (handler.HasMarker && live.Mark != mark)
            {
                changed.Add(ResourceHandler.MarkerField);
                updated.Add($"marker {live.Mark?.Text ?? "(none)"} -> {mark.Text}");
            }

            var changes = new List<Change>();
            if (replaced.Count > 0)
            {
                if (node.Protect || node.Exported || live.Mark is { Keeps: true })
                    Problem($"{CloudInventory.Describe(node, handler)} is {(node.Protect || live.Mark is { Protect: true } ? "protected" : "exported")}; replacing it would destroy it ({string.Join("; ", replaced)})");
                state.Pend(node.Id);
                changes.Add(new Change(ChangeMarker.Replace, "replace: " + string.Join("; ", replaced.Concat(updated))));
                Diffs.Add(new ResourceDiff(ResourceAction.Replace, handler, session, node, live, changed));
            }
            else if (updated.Count > 0)
            {
                changes.Add(Change.Update(string.Join("; ", updated)));
                Diffs.Add(new ResourceDiff(ResourceAction.Update, handler, session, node, live, changed));
            }

            changes.AddRange(notes);
            if (changes.Count > 0)
                Changes.Add(new ResourceChange(session.Provider.Kind, handler.Kind, handler.Display(node), changes));
        }

        static string Describe(ResourceHandler handler, IReadOnlyDictionary<string, string?> desired)
        {
            var parts = handler.Fields
                .Where(field => desired.TryGetValue(field.Name, out var value) && value is not ("" or "[]" or "false"))
                .Select(field => $"{field.Name} {Show(field, desired[field.Name])}")
                .ToList();
            return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
        }

        static string Show(FieldSpec field, string? value) => value switch
        {
            null => InfraValue.UnknownDisplay,
            _ when field.Sensitive => InfraValue.SecretDisplay,
            "" => "(none)",
            _ => value,
        };
    }
}
