using Npgsql;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record PgLogin(string User, string Password);

public sealed record PgEndpoint(string Host, int Port, string Database, PgLogin Login, bool Ssl = false)
{
    public const string DockerHost = "host.docker.internal";
    const string DefaultAdminDatabase = "postgres";

    public PgLogin Admin { get; init; } = Login;

    public string AdminDatabase { get; init; } = DefaultAdminDatabase;

    public string? Cluster { get; init; }

    public bool SeparateAdmin => Admin.User != Login.User;

    public string Display => $"{Host}:{Port}/{Database}";

    public string ConnectionString(string? database = null, bool admin = false)
    {
        var login = admin ? Admin : Login;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = database ?? (admin ? AdminDatabase : Database),
            Username = login.User,
            Password = login.Password,
            SslMode = Ssl ? SslMode.Require : SslMode.Disable,
            Pooling = false,
            Timeout = 15,
        }.ConnectionString;
    }

    public IReadOnlyDictionary<string, string> Environment(string database) => new Dictionary<string, string>
    {
        ["PGHOST"] = ContainerHost,
        ["PGPORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["PGUSER"] = Login.User,
        ["PGPASSWORD"] = Login.Password,
        ["PGDATABASE"] = database,
        ["PGSSLMODE"] = Ssl ? "require" : "disable",
    };

    public string ContainerHost => IsLoopback(Host) ? DockerHost : Host;

    public bool SameDatabaseAs(PgEndpoint other) =>
        string.Equals(ContainerHost, other.ContainerHost, StringComparison.OrdinalIgnoreCase) && Port == other.Port && Database == other.Database;

    public static bool IsLoopback(string host) => host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    public static PgEndpoint Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql"))
            throw new DatabaseCopyException($"\"{Redacted(url)}\" is not a postgres:// URL");

        var userInfo = uri.UserInfo.Split(':', 2);
        var login = new PgLogin(Uri.UnescapeDataString(userInfo[0]), userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "");
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var ssl = uri.Query.Contains("sslmode=require", StringComparison.OrdinalIgnoreCase);
        return new PgEndpoint(uri.Host, uri.Port > 0 ? uri.Port : PostgresServices.Port, database, login, ssl)
        {
            AdminDatabase = database.Length > 0 ? database : DefaultAdminDatabase,
        };
    }

    static string Redacted(string url) => url.IndexOf('@') is var at and > 0 && url.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0 && scheme < at
        ? url[..(scheme + 3)] + "(secret)" + url[at..]
        : url;
}

public class DatabaseCopyException(string message) : Exception(message);
