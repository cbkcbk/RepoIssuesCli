using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Postgres;

public delegate Task<IHostConnection> HostLookup(NodeId host, CancellationToken cancellationToken);

public sealed class DatabaseEndpoints(InfraGraph graph, ILiveValues live, SecretLookup secrets, HostLookup hosts, InfraLog log) : IDisposable
{
    public const string LocalPrefix = "local:";
    public const string LocalVariable = "INFRA_LOCAL_PG";
    public const string DefaultLocal = "postgres://postgres@localhost:5432";
    public const int DirectPort = 25060;

    readonly List<IPortForward> forwards = [];

    public static PgEndpoint Local(string database, string? url = null)
    {
        var server = PgEndpoint.Parse(url ?? Environment.GetEnvironmentVariable(LocalVariable) ?? DefaultLocal);
        return server with { Database = database };
    }

    public async Task<PgEndpoint> ResolveAsync(NodeId database, CancellationToken cancellationToken)
    {
        if (graph.Resource(database) is { Kind: DatabaseHandler.KindName, Parent: { } cluster } managed)
            return Managed(managed, graph.Resource(cluster)!);
        if (graph.Component(database) is { } component && PostgresServices.Databases(graph).Contains(component))
            return await SelfHostedAsync(component, cancellationToken);
        throw new DatabaseCopyException($"{database} is not a database declared in this repo");
    }

    PgEndpoint Managed(ResourceNode database, ResourceNode cluster)
    {
        var display = $"{cluster.Name}/{database.Name}";
        var connection = Text(database.Id, SelfHostedDatabases.ConnectionStringField)
                         ?? throw new DatabaseCopyException($"database {display} is not live (or its user is missing); apply creates it");
        var admin = Text(database.Id, DatabaseHandler.AdminConnectionStringField)
                    ?? throw new DatabaseCopyException($"database cluster {cluster.Name} returned no admin connection");
        var user = PgEndpoint.Parse(connection);
        var owner = PgEndpoint.Parse(admin);
        Secret(user.Login.Password);
        Secret(owner.Login.Password);
        return user with { Port = DirectPort, Ssl = true, Admin = owner.Login, AdminDatabase = owner.Database, Cluster = cluster.Name };
    }

    async Task<PgEndpoint> SelfHostedAsync(ComponentNode database, CancellationToken cancellationToken)
    {
        if (PostgresServices.Servers(graph).Where(server => server.DatabaseId == database.Id).ToList() is not [var server])
            throw new DatabaseCopyException($"postgres.database \"{database.Name}\" needs exactly one postgres.server to copy through");
        var network = database.Options["network"] is NodeRef reference && graph.Component(reference.Node) is { } node
            ? node.Name
            : throw new DatabaseCopyException($"postgres.database \"{database.Name}\" has no network");

        var connection = await hosts(server.Compose.Host, cancellationToken);
        var address = await ContainerAddressAsync(connection, database.Name, network, cancellationToken);
        var forward = await connection.ForwardAsync(address, PostgresServices.Port, cancellationToken);
        forwards.Add(forward);

        var user = database.Options.GetString("user") ?? database.Name;
        var password = database.Options["password"] is SecretRef secret ? secrets.Get(secret) : "";
        Secret(password);
        return new PgEndpoint("localhost", forward.LocalPort, database.Name, new PgLogin(user, password));
    }

    public static async Task<string> ContainerAddressAsync(IHostConnection connection, string database, string network, CancellationToken cancellationToken)
    {
        var label = $"label={PostgresServices.ServerLabel}={database}";
        var listed = await connection.RunCheckedAsync("docker ps", $"docker ps -q --filter {Sh.Quote(label)}", cancellationToken);
        var ids = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids is not [var id])
            throw new DatabaseCopyException(ids.Length == 0
                ? $"no running container on {connection.Host.Name} has the label {PostgresServices.ServerLabel}={database}; apply the play that runs its postgres.server first"
                : $"{ids.Length} containers on {connection.Host.Name} have the label {PostgresServices.ServerLabel}={database}");

        var template = $"{{{{with index .NetworkSettings.Networks {Go(network)}}}}}{{{{.IPAddress}}}}{{{{end}}}}";
        var inspected = await connection.RunCheckedAsync("docker inspect", $"docker inspect -f {Sh.Quote(template)} {Sh.Quote(id)}", cancellationToken);
        return inspected.Stdout.Trim() is { Length: > 0 } address
            ? address
            : throw new DatabaseCopyException($"the postgres.server container for \"{database}\" on {connection.Host.Name} is not on the network {network}");
    }

    static string Go(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    string? Text(NodeId node, string field) => live.Resolve(new OutputRef(node, field, true)) is StringValue { Value.Length: > 0 } text ? text.Value : null;

    void Secret(string value)
    {
        if (value.Length > 0)
            log.AddSecret(value);
    }

    public void Dispose()
    {
        foreach (var forward in forwards)
            forward.Dispose();
        forwards.Clear();
    }
}
