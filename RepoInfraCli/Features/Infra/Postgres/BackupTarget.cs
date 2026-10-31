using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record BackupTarget(
    string Database,
    HostNode Host,
    string Play,
    string Role,
    string Project,
    string Directory,
    string Service,
    string Location,
    IReadOnlyList<string> Apps)
{
    public string Where => $"{Host.Name} (play {Play} > {Role}, compose {Project} service {Service})";

    public static BackupTarget Find(InfraGraph graph, string database)
    {
        var backups = PostgresServices.Backups(graph).Where(backup => backup.Database == database).ToList();
        if (backups.Count == 0)
            throw Missing(graph, database);
        if (backups.Count > 1)
            throw new RestoreRefusedException(
                $"\"{database}\" is backed up by {backups.Count} postgres.backup services ({string.Join(", ", backups.Select(backup => $"{backup.Compose.Host} compose {backup.Compose.Name} service {backup.Service}"))}); restore needs exactly one.");

        var backup = backups[0];
        var host = graph.Host(backup.Compose.Host) ?? throw new InvalidOperationException($"host {backup.Compose.Host} of postgres.backup \"{database}\" is not in the graph");
        if (host.External)
            throw new RestoreRefusedException($"The backups of \"{database}\" run on {host.Name}, a host {Planner.UnresolvedNote}; fetch it first, or restore from the repo that declares {host.Name}.");

        var (play, run) = graph.Plays
            .SelectMany(node => node.Runs.Select(roleRun => (node.Name, roleRun)))
            .First(pair => pair.roleRun.Tasks.Any(task => task.Id == backup.Compose.Id));
        var project = ComposeDocument.ProjectName(backup.Compose.Name ?? "");
        var bucket = backup.Environment.GetString(BackupScript.Bucket);
        var prefix = backup.Environment.GetString(BackupScript.Prefix);
        return new BackupTarget(
            database,
            host,
            play,
            run.Role,
            project,
            ComposeDocument.ProjectDirectory(project, backup.Compose.Options),
            backup.Service,
            bucket is null ? $"{prefix}/" : $"{bucket}/{prefix}/",
            Users(graph, backup.DatabaseId));
    }

    static List<string> Users(InfraGraph graph, NodeId database) =>
    [
        .. graph.Plays.SelectMany(play => play.Runs)
            .Where(run => run.Role.StartsWith(RoleRun.AppPrefix, StringComparison.Ordinal)
                          && run.Tasks.Any(task => task.Options.References().Contains(database)))
            .Select(run => run.Role[RoleRun.AppPrefix.Length..])
            .Distinct(),
    ];

    static Exception Missing(InfraGraph graph, string database)
    {
        if (graph.Components.Any(component => component.Kind == NodeKinds.Database && component.External && component.Name == database)
            || graph.Resources.Any(resource => resource.Kind == DatabaseHandler.KindName && resource.Reference is not null && resource.Name == database))
            return new RestoreRefusedException(
                $"\"{database}\" is declared in another repo (repo.provider); restore it from the repo that declares it, or copy its data with 'infra db copy {database} --to <database>'.");

        if (graph.Resources.Any(resource => resource.Kind == DatabaseHandler.KindName && resource.Name == database))
            return new RestoreRefusedException(
                $"\"{database}\" is a managed DigitalOcean database; infra restore only restores self-hosted postgres. DigitalOcean backs up and restores managed databases (its point-in-time restore); to copy its data elsewhere, use 'infra db copy {database} --to <database>'.");

        if (graph.Components.Any(component => component.Id == PostgresServices.DatabaseId(database)))
            return new BackupNotFoundException(
                $"postgres.database \"{database}\" has no postgres.backup; add postgres.backup(db) {{ bucket = ..., credentials = ..., schedule = ... }} next to its postgres.server.");

        var backedUp = PostgresServices.Backups(graph).Select(backup => backup.Database).Distinct().ToList();
        return new BackupNotFoundException(
            $"No database named '{database}'." + (backedUp.Count == 0 ? " No postgres.backup is declared." : $" Databases with backups: {string.Join(", ", backedUp)}."));
    }
}

public sealed class RestoreRefusedException(string message) : Exception(message);
