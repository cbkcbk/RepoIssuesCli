using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Postgres;

public static class PostgresValidator
{
    public static IEnumerable<InfraDiagnostic> Validate(InfraGraph graph)
    {
        var servers = PostgresServices.Servers(graph).ToLookup(server => server.DatabaseId);
        var referenced = graph.Tasks.SelectMany(task => task.Options.References())
            .Concat(graph.Exports?.Nodes ?? [])
            .Concat(graph.Copies.SelectMany(copy => (NodeId[])[copy.From, copy.To]))
            .ToHashSet();

        foreach (var database in PostgresServices.Databases(graph))
        {
            var running = servers[database.Id].ToList();
            if (running.Count > 1)
                yield return InfraDiagnostic.Error(DiagnosticCodes.InvalidValue,
                    $"postgres.database \"{database.Name}\" is run by {running.Count} postgres.server services ({string.Join(", ", running.Select(Describe))}); run it once so its host is unambiguous",
                    database.Location);
            else if (running.Count == 0 && referenced.Contains(database.Id))
                yield return InfraDiagnostic.Error(DiagnosticCodes.InvalidValue,
                    $"postgres.database \"{database.Name}\" is used but no postgres.server runs it; add postgres.server(db) to an h:compose services table",
                    database.Location);
        }

        foreach (var backup in PostgresServices.Backups(graph))
        {
            if (servers[backup.DatabaseId].ToList() is not [var server] || server.Compose.Host == backup.Compose.Host)
                continue;
            yield return InfraDiagnostic.Error(DiagnosticCodes.InvalidValue,
                $"postgres.backup of \"{backup.Database}\" runs on {backup.Compose.Host} but its postgres.server runs on {server.Compose.Host}; put the backup on the same host so it reaches the server over the database network",
                backup.Compose.Location);
        }

        foreach (var twice in graph.Copies.GroupBy(copy => copy.To).Where(group => group.Count() > 1))
            yield return InfraDiagnostic.Error(DiagnosticCodes.InvalidValue,
                $"postgres.copy writes \"{DeclaredDatabases.Display(graph, twice.Key)}\" {twice.Count()} times ({string.Join(", ", twice.Select(copy => copy.Location?.ToString() ?? copy.Id.Value))}); each apply would overwrite the earlier copy",
                twice.Last().Location);
    }

    static string Describe(PostgresService service) => $"{service.Compose.Host} compose {service.Compose.Name} service {service.Service}";
}
