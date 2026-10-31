using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Plan;

public sealed record ResourcePlan(IReadOnlyList<ResourceChange> Changes, IReadOnlyList<PlanProblem> Problems)
{
    public static ResourcePlan Empty { get; } = new([], []);
}

public interface ILiveResourcePlanner
{
    Task<ResourcePlan> PlanAsync(PlanRun run, PlanFilter filter, CancellationToken cancellationToken);
}

public interface IResourceApplier
{
    Task<ApplyResult> ApplyAsync(PlanRun run, InfraLog log, Action<ApplyStep>? progress, CancellationToken cancellationToken);
}

public sealed class NoResourceApplier : IResourceApplier
{
    public static NoResourceApplier Instance { get; } = new();

    public Task<ApplyResult> ApplyAsync(PlanRun run, InfraLog log, Action<ApplyStep>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(new ApplyResult([]));
}
