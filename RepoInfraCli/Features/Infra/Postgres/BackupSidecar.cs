using System.Text;
using RepoInfraCli.Features.Infra.Hosts;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed class BackupSidecar(IHostConnection connection, BackupTarget target)
{
    public async Task<IReadOnlyList<BackupDump>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await connection.RunCheckedAsync($"listing the backups of {target.Database}", Script([BackupScript.ListCommand]), cancellationToken);
        return BackupDumps.Parse(result.Stdout);
    }

    public Task RestoreAsync(BackupDump dump, string? into, CancellationToken cancellationToken = default) =>
        connection.RunCheckedAsync($"restoring {dump.Key} into {into ?? target.Database}",
            Script([BackupScript.RestoreCommand, dump.Key, .. into is null ? Array.Empty<string>() : [into]]), cancellationToken);

    public string Script(IEnumerable<string> arguments) => new StringBuilder()
        .AppendLine("set -eu")
        .AppendLine($"d={Sh.Quote(target.Directory)}")
        .AppendLine($"[ -f \"$d/compose.yaml\" ] || {{ echo \"$d/compose.yaml does not exist; run infra apply first\" >&2; exit 1; }}")
        .AppendLine("cd \"$d\"")
        .AppendLine($"docker compose -p {Sh.Quote(target.Project)} run --rm -T {Sh.Quote(target.Service)} {Sh.Join(arguments)}")
        .ToString();
}
