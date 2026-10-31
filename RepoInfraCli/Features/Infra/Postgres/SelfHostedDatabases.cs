using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed class SelfHostedDatabases(ILiveValues inner, InfraGraph graph, SecretLookup secrets) : ILiveValues
{
    public const string HostField = "host";
    public const string ConnectionStringField = "connection_string";

    readonly Dictionary<NodeId, ComponentNode> databases = PostgresServices.Databases(graph).ToDictionary(database => database.Id);

    readonly Dictionary<NodeId, string> hosts = PostgresServices.Servers(graph)
        .GroupBy(server => server.DatabaseId)
        .Where(group => group.Count() == 1)
        .ToDictionary(group => group.Key, group => group.Single().Service);

    public InfraValue? Resolve(OutputRef output)
    {
        if (!databases.TryGetValue(output.Node, out var database) || !hosts.TryGetValue(output.Node, out var host))
            return inner.Resolve(output);
        return output.Field switch
        {
            HostField => new StringValue(host),
            ConnectionStringField => new StringValue(ConnectionString(database, host)),
            _ => inner.Resolve(output),
        };
    }

    public bool CreatedThisRun(NodeId resource) => inner.CreatedThisRun(resource);

    string ConnectionString(ComponentNode database, string host)
    {
        var user = database.Options.GetString("user") ?? database.Name;
        var password = database.Options["password"] is SecretRef secret ? secrets.Get(secret) : "";
        return $"postgresql://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{host}:{PostgresServices.Port}/{Uri.EscapeDataString(database.Name)}";
    }
}
