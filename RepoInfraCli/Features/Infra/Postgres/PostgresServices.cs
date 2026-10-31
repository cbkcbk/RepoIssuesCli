using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record PostgresService(string Database, TaskNode Compose, string Service, MapValue Options)
{
    public NodeId DatabaseId => PostgresServices.DatabaseId(Database);

    public MapValue Environment => Options["environment"] as MapValue ?? MapValue.Empty;
}

public static class PostgresServices
{
    public const string DatabaseKind = "postgres.database";
    public const string ServerLabel = "repo-infra.postgres.server";
    public const string BackupLabel = "repo-infra.postgres.backup";
    public const int Port = 5432;

    public static NodeId DatabaseId(string name) => NodeId.Of(DatabaseKind, name);

    public static IEnumerable<ComponentNode> Databases(InfraGraph graph) =>
        graph.Components.Where(component => component.Id.Value.StartsWith(DatabaseKind + ":", StringComparison.Ordinal));

    public static IEnumerable<PostgresService> Servers(InfraGraph graph) => Labelled(graph, ServerLabel);

    public static IEnumerable<PostgresService> Backups(InfraGraph graph) => Labelled(graph, BackupLabel);

    static IEnumerable<PostgresService> Labelled(InfraGraph graph, string label) =>
        from task in graph.Tasks
        where task.Kind == TaskKinds.Compose
        from service in (task.Options["services"] as MapValue ?? MapValue.Empty).Entries
        let options = service.Value as MapValue ?? MapValue.Empty
        let database = (options["labels"] as MapValue)?.GetString(label)
        where database is not null
        select new PostgresService(database, task, service.Key, options);
}
