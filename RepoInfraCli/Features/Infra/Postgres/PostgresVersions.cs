namespace RepoInfraCli.Features.Infra.Postgres;

public static class PostgresVersions
{
    public static int? Major(string? version) =>
        int.TryParse(string.Concat((version ?? "").TakeWhile(char.IsAsciiDigit)), out var major) ? major : null;

    public static int MajorOf(int serverVersionNum) => serverVersionNum / 10000;
}
