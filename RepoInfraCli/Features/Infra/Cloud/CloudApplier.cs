using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed class CloudApplier(CloudServices cloud) : IResourceApplier
{
    public const string Phase = "resources";

    public async Task<ApplyResult> ApplyAsync(PlanRun run, InfraLog log, Action<ApplyStep>? progress, CancellationToken cancellationToken)
    {
        var steps = new List<ApplyStep>();
        var values = new CloudValues(cloud.State);
        var marker = run.Graph.Marker;
        foreach (var diff in cloud.State.Diffs)
        {
            ApplyStep step;
            try
            {
                await ApplyAsync(diff, values, marker, cancellationToken);
                step = Step(diff, ApplyOutcome.Applied);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                step = Step(diff, ApplyOutcome.Failed, exception.Message);
            }

            steps.Add(step);
            log.Write(step.Outcome == ApplyOutcome.Failed ? InfraLogLevel.Error : InfraLogLevel.Debug, "apply.resource",
                $"{step.Host} {step.Role} {step.Label}: {step.Error ?? "applied"}",
                new Dictionary<string, object?> { ["provider"] = step.Host, ["kind"] = step.Role, ["resource"] = diff.Name, ["action"] = diff.Action.ToString(), ["outcome"] = step.Outcome.ToString() });
            progress?.Invoke(step);
            if (step.Outcome == ApplyOutcome.Failed)
                break;
        }
        return new ApplyResult(steps);
    }

    async Task ApplyAsync(ResourceDiff diff, CloudValues values, Marker marker, CancellationToken cancellationToken)
    {
        switch (diff.Action)
        {
            case ResourceAction.Create:
                await CreateAsync(diff, values, marker, recheck: true, cancellationToken);
                break;
            case ResourceAction.Replace:
                await diff.Handler.DeleteAsync(diff.Session, diff.Live!, cancellationToken);
                await CreateAsync(diff, values, marker, recheck: false, cancellationToken);
                break;
            case ResourceAction.Update:
                var updated = await diff.Handler.UpdateAsync(Context(diff, values, marker), diff.Live!, diff.Changed, cancellationToken);
                cloud.State.Applied(diff.Node!.Id, updated, isNew: false);
                break;
            case ResourceAction.Delete:
                await diff.Handler.DeleteAsync(diff.Session, diff.Live!, cancellationToken);
                break;
        }
    }

    async Task CreateAsync(ResourceDiff diff, CloudValues values, Marker marker, bool recheck, CancellationToken cancellationToken)
    {
        var node = diff.Node!;
        if (recheck && diff.Handler.ParentKind is null)
        {
            var fresh = await diff.Handler.ListAsync(diff.Session, marker, cancellationToken);
            if ((diff.Handler.FindOwned(node, fresh, marker) ?? diff.Handler.FindUnowned(node, fresh, marker)) is { } appeared)
                throw new CloudApiException($"{appeared.Name} (id {appeared.Id}) appeared after the plan was made; run plan again");
        }
        var created = await diff.Handler.CreateAsync(Context(diff, values, marker), cancellationToken);
        cloud.State.Applied(node.Id, created, isNew: true);
    }

    ResourceContext Context(ResourceDiff diff, CloudValues values, Marker marker)
    {
        var node = diff.Node!;
        var parent = node.Parent is { } parentId ? cloud.State.Live(parentId) : null;
        return new ResourceContext(diff.Session, node, parent, values, OwnerMark.For(node, marker), marker);
    }

    static ApplyStep Step(ResourceDiff diff, ApplyOutcome outcome, string? error = null) =>
        new(Phase, diff.Session.Provider.Kind, diff.Handler.Kind, $"{Verb(diff.Action)} {diff.Name}", outcome, error);

    static string Verb(ResourceAction action) => action switch
    {
        ResourceAction.Create => "create",
        ResourceAction.Update => "update",
        ResourceAction.Replace => "replace",
        _ => "delete",
    };
}
