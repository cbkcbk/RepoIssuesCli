using System.Net.Sockets;
using Npgsql;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Postgres;

public enum Confirmation
{
    NotNeeded,
    Given,
    Ask,
    Missing,
    Wrong,
}

public static class CopyRules
{
    public static string? SameDatabase(PgEndpoint source, PgEndpoint target) =>
        source.SameDatabaseAs(target) ? $"source and target are the same database ({target.Display}); refusing to copy a database onto itself" : null;

    public static string? VersionMismatch(PgEndpoint source, int? declared, int actual) =>
        declared is { } expected && expected != actual
            ? $"{source.Display} runs postgres {actual} but is declared as version {expected}; fix the declared version before copying"
            : null;

    public static string? ScrubRequired(bool cloudSource, bool localTarget, bool hasScrub) =>
        cloudSource && localTarget && !hasScrub
            ? "copying a cloud database to local: needs --scrub <file.sql> so production data is scrubbed before it reaches this machine"
            : null;

    public static Confirmation Confirm(bool isProtected, string owner, string? confirm, bool interactive) =>
        (isProtected, confirm) switch
        {
            (false, _) => Confirmation.NotNeeded,
            (true, { } given) => given == owner ? Confirmation.Given : Confirmation.Wrong,
            (true, null) => interactive ? Confirmation.Ask : Confirmation.Missing,
        };

    public static bool IsProtected(InfraGraph graph, NodeId database)
    {
        if (graph.Exports?.Databases.Values.Contains(database) == true)
            return true;
        if (graph.Resource(database) is not { } resource)
            return false;
        return resource.Protect || resource.Exported || (resource.Parent is { } parent && graph.Resource(parent) is { Protect: true } or { Exported: true });
    }

    public static string ConnectFailure(PgEndpoint endpoint, string user, NpgsqlException exception) =>
        endpoint.Cluster is { } cluster && TimedOut(exception)
            ? $"connecting to {endpoint.Host}:{endpoint.Port} timed out; add this machine's IP to the trusted_sources of database cluster \"{cluster}\""
            : $"can't connect to {endpoint.Display} as {user}: {exception.Message}";

    static bool TimedOut(Exception exception) =>
        exception is TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut }
        || (exception.InnerException is { } inner && TimedOut(inner));
}
