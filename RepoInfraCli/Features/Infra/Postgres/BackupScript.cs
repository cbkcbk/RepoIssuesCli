using System.Security.Cryptography;
using System.Text;

namespace RepoInfraCli.Features.Infra.Postgres;

public static class BackupScript
{
    public const string ReadyFile = "/tmp/infra-backup.ready";
    public const string ScheduleCommand = "schedule";
    public const string BackupCommand = "backup";
    public const string ListCommand = "list";
    public const string RestoreCommand = "restore";

    public const string Endpoint = "BACKUP_ENDPOINT";
    public const string Region = "BACKUP_REGION";
    public const string Bucket = "BACKUP_BUCKET";
    public const string Prefix = "BACKUP_PREFIX";
    public const string Schedule = "BACKUP_SCHEDULE";
    public const string Credentials = "BACKUP_CREDENTIALS";

    public static IReadOnlyList<string> Commands { get; } = [ScheduleCommand, BackupCommand, ListCommand, RestoreCommand];

    public static string Content { get; } = Load();

    public static string HostPath { get; } = $"/usr/local/lib/repo-infra/postgres-backup-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Content)))[..12]}";

    public static IEnumerable<string> Mounts => Commands.Select(command => $"{HostPath}:/usr/local/bin/{command}:ro");

    static string Load()
    {
        using var stream = typeof(BackupScript).Assembly.GetManifestResourceStream("postgres-backup.sh")
                           ?? throw new InvalidOperationException("the postgres backup script is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
