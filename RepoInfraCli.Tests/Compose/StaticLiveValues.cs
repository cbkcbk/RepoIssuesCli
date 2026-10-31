using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Tests.Compose;

public sealed class StaticLiveValues(params (string Node, string Field, string Value)[] values) : ILiveValues
{
    readonly Dictionary<(string, string), string> known = values.ToDictionary(value => (value.Node, value.Field), value => value.Value);

    public InfraValue? Resolve(OutputRef output) =>
        known.TryGetValue((output.Node.Value, output.Field), out var value) ? new StringValue(value) : null;

    public bool CreatedThisRun(NodeId resource) => false;
}
