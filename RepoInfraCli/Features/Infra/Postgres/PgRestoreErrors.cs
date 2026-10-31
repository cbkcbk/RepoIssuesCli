using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra.Postgres;

public static partial class PgRestoreErrors
{
    static readonly Regex[] Harmless =
    [
        MustOwnExtension(),
        ExtensionExists(),
        PublicSchemaExists(),
        MustOwnPublicSchema(),
    ];

    public static IReadOnlyList<string> Fatal(string stderr) =>
    [
        .. stderr.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("pg_restore: error:", StringComparison.Ordinal))
            .Where(line => !IsHarmless(line)),
    ];

    public static bool IsHarmless(string line) =>
        QueryError().Match(line) is { Success: true } match && Harmless.Any(pattern => pattern.IsMatch(match.Groups["message"].Value));

    [GeneratedRegex(@"could not execute query: ERROR:\s+(?<message>.*)$")]
    private static partial Regex QueryError();

    [GeneratedRegex(@"^must be owner of extension [A-Za-z0-9_""-]+$")]
    private static partial Regex MustOwnExtension();

    [GeneratedRegex(@"^extension ""[^""]+"" already exists$")]
    private static partial Regex ExtensionExists();

    [GeneratedRegex(@"^schema ""public"" already exists$")]
    private static partial Regex PublicSchemaExists();

    [GeneratedRegex(@"^must be owner of schema public$")]
    private static partial Regex MustOwnPublicSchema();
}
