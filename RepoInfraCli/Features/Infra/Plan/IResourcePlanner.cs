using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Plan;

public interface IResourcePlanner
{
    Task<IReadOnlyList<ResourceChange>> PlanAsync(InfraGraph graph, PlanFilter filter, CancellationToken cancellationToken);
}

public sealed class NoResourcePlanner : IResourcePlanner
{
    public static NoResourcePlanner Instance { get; } = new();

    public Task<IReadOnlyList<ResourceChange>> PlanAsync(InfraGraph graph, PlanFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ResourceChange>>([]);
}
