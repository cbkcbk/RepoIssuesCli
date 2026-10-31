using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Plan;

public sealed class PlanRun(InfraGraph graph, InfraWorkspace workspace, ILiveValues live, SecretLookup secrets) : IAsyncDisposable
{
    public InfraGraph Graph => graph;

    public InfraWorkspace Workspace => workspace;

    public ILiveValues Live => live;

    public SecretLookup Secrets => secrets;

    public InfraPlan Plan { get; internal set; } = new([], [], [], true);

    public PlanOptions? Options { get; internal set; }

    public Dictionary<NodeId, IHostConnection> Connections { get; } = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in Connections.Values)
            await connection.DisposeAsync();
        Connections.Clear();
    }
}