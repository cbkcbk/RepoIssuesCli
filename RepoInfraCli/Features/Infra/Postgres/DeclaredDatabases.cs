using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record DeclaredDatabase(NodeId Id, string Name, string Display, bool Managed, int? Major)
{
    public Marker? Owner { get; init; }
}

public static class DeclaredDatabases
{
    public static IReadOnlyList<DeclaredDatabase> All(InfraGraph graph) =>
    [
        .. PostgresServices.Databases(graph)
            .Select(database => new DeclaredDatabase(database.Id, database.Name, database.Name, false, PostgresVersions.Major(database.Options.GetString("version")))),
        .. graph.Resources.Where(resource => resource.Kind == DatabaseHandler.KindName && resource.Parent is not null).Select(database =>
        {
            var cluster = graph.Resource(database.Parent!.Value);
            return new DeclaredDatabase(database.Id, database.Name, $"{cluster?.Name}/{database.Name}", true, PostgresVersions.Major(cluster?.Options.GetString("version")))
            {
                Owner = database.Reference,
            };
        }),
    ];

    public static DeclaredDatabase? Of(InfraGraph graph, NodeId id) => All(graph).FirstOrDefault(database => database.Id == id);

    public static string Display(InfraGraph graph, NodeId id) => Of(graph, id)?.Display ?? id.Value;

    public static DeclaredDatabase Find(InfraGraph graph, string name)
    {
        var all = All(graph);
        var matches = all.Where(database => database.Name == name || database.Display == name).ToList();
        if (matches is [var single])
            return single;
        if (matches.Count > 1)
            throw new UsageException($"'{name}' matches {List(matches.Select(match => match.Display))}; name the cluster too, as cluster/database.");

        if (graph.Resources.FirstOrDefault(resource => resource.Kind == DatabaseClusterHandler.KindName && resource.Name == name) is { } cluster)
        {
            var databases = all.Where(database => graph.Resource(database.Id)?.Parent == cluster.Id).Select(database => database.Display);
            throw new UsageException($"'{name}' is a database cluster, not a database; copy one of its databases: {List(databases)}.");
        }
        if (PostgresServices.Servers(graph).FirstOrDefault(server => server.Service == name) is { } running)
            throw new UsageException($"'{name}' is a postgres.server, not a database; copy the database it runs: {running.Database}.");
        throw new DatabaseNotFoundException($"No database named '{name}'. Databases: {List(all.Select(database => database.Display))}.");
    }

    static string List(IEnumerable<string> names) => string.Join(", ", names) is { Length: > 0 } joined ? joined : "(none declared)";
}

public sealed class DatabaseNotFoundException(string message) : NotFoundException(message);
