using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Plan;

public interface ILiveValues
{
    InfraValue? Resolve(OutputRef output);

    bool CreatedThisRun(NodeId resource);
}

public sealed class NoLiveValues : ILiveValues
{
    public static NoLiveValues Instance { get; } = new();

    public InfraValue? Resolve(OutputRef output) => null;

    public bool CreatedThisRun(NodeId resource) => false;
}
