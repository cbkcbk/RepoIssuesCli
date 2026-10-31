using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Npgsql;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record CopyRequest(PgEndpoint Source, PgEndpoint Target)
{
    public int? DeclaredMajor { get; init; }

    public string? Scrub { get; init; }

    public string? MigrationsProject { get; init; }
}

public sealed record CopyContainers(string Dump, string Restore)
{
    public IEnumerable<string> All => [Dump, Restore];
}

public sealed class DatabaseCopier(IProcessRunner runner, TimeProvider? clock = null)
{
    const int MaxIdentifierBytes = 63;

    readonly TimeProvider time = clock ?? TimeProvider.System;

    public static DatabaseCopier Default { get; } = new(ProcessRunner.Instance);

    public string TempName(string database) => $"{database}__infra_tmp_{time.GetUtcNow():yyyyMMddHHmmss}";

    public static CopyContainers Containers(string temp)
    {
        var safe = string.Concat(temp.ToLowerInvariant().Select(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '.' or '-' ? letter : '-'));
        return new CopyContainers($"infra-copy-{safe}-dump", $"infra-copy-{safe}-restore");
    }

    public async Task CopyAsync(CopyRequest request, InfraLog log, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        void Step(string text)
        {
            log.Info("db.copy.step", text);
            progress?.Invoke(text);
        }

        var (source, target) = (request.Source, request.Target);
        if (CopyRules.SameDatabase(source, target) is { } same)
            throw new DatabaseCopyException(same);
        var major = await SourceMajorAsync(source, request.DeclaredMajor, cancellationToken);

        var temp = TempName(target.Database);
        if (Encoding.UTF8.GetByteCount(temp) > MaxIdentifierBytes)
            throw new DatabaseCopyException($"the temporary database name {temp} is longer than postgres allows ({MaxIdentifierBytes} bytes); use a shorter target name");
        var containers = Containers(temp);
        var dropped = false;
        try
        {
            Step($"create {temp} on {target.Host}:{target.Port}");
            await CreateAsync(target, temp, cancellationToken);
            Step($"pg_dump {source.Display} | pg_restore {temp} (postgres:{major})");
            await PipeAsync(source, target, temp, major, containers, log, cancellationToken);
            if (request.Scrub is { } scrub)
            {
                Step($"scrub {temp} with {Path.GetFileName(scrub)}");
                await ScrubAsync(target, temp, scrub, cancellationToken);
            }

            if (request.MigrationsProject is { } project)
            {
                Step($"migrate {temp} with {Path.GetFileName(project)}");
                await MigrateAsync(target, temp, project, log, cancellationToken);
            }

            Step($"replace {target.Database} with {temp}");
            await using var admin = await OpenAsync(target, null, true, CancellationToken.None);
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {Quote(target.Database)} WITH (FORCE)", CancellationToken.None);
            dropped = true;
            await ExecuteAsync(admin, $"ALTER DATABASE {Quote(temp)} RENAME TO {Quote(target.Database)}", CancellationToken.None);
        }
        catch (Exception exception) when (dropped && exception is not OperationCanceledException)
        {
            throw new DatabaseCopyException($"dropped {target.Database} but could not rename {temp} to it: {exception.Message}; the copy is complete in {temp}, rename it by hand");
        }
        catch when (!dropped)
        {
            await CleanUpAsync(target, temp, containers, log);
            throw;
        }
    }

    async Task<int> SourceMajorAsync(PgEndpoint source, int? declared, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(source, null, false, cancellationToken);
        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var major = PostgresVersions.MajorOf(int.Parse((string)(await command.ExecuteScalarAsync(cancellationToken))!, System.Globalization.CultureInfo.InvariantCulture));
        if (CopyRules.VersionMismatch(source, declared, major) is { } mismatch)
            throw new DatabaseCopyException(mismatch);
        return major;
    }

    static async Task CreateAsync(PgEndpoint target, string temp, CancellationToken cancellationToken)
    {
        await using (var admin = await OpenAsync(target, null, true, cancellationToken))
        {
            await ExecuteAsync(admin, $"CREATE DATABASE {Quote(temp)}", cancellationToken);
            if (!target.SeparateAdmin)
                return;
            await ExecuteAsync(admin, $"GRANT ALL PRIVILEGES ON DATABASE {Quote(temp)} TO {Quote(target.Login.User)}", cancellationToken);
        }

        await using var inside = await OpenAsync(target, temp, true, cancellationToken);
        await ExecuteAsync(inside, $"GRANT ALL ON SCHEMA public TO {Quote(target.Login.User)}", cancellationToken);
    }

    static async Task PipeAsync(PgEndpoint source, PgEndpoint target, string temp, int major, CopyContainers containers, InfraLog log, CancellationToken cancellationToken)
    {
        var image = $"postgres:{major}";
        var dumpEnvironment = source.Environment(source.Database);
        var restoreEnvironment = target.Environment(temp);
        var dumpArguments = DockerRun(containers.Dump, image, dumpEnvironment, false, ["pg_dump", "-Fc", "--no-owner", "--no-acl"]);
        var restoreArguments = DockerRun(containers.Restore, image, restoreEnvironment, true, ["pg_restore", "--no-owner", "--no-acl", "--dbname", temp]);
        var dumpCall = ProcessTraffic.Start(log, "docker", dumpArguments, null, dumpEnvironment.Keys, null);
        using var dump = Start(dumpArguments, dumpEnvironment, false);
        var restoreCall = ProcessTraffic.Start(log, "docker", restoreArguments, null, restoreEnvironment.Keys, "(pg_dump output, streamed)");
        using var restore = Start(restoreArguments, restoreEnvironment, true);
        var dumpErrors = dump.StandardError.ReadToEndAsync(cancellationToken);
        var restoreOutput = restore.StandardOutput.ReadToEndAsync(cancellationToken);
        var restoreErrors = restore.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await PumpAsync(dump, restore, cancellationToken);
            await dump.WaitForExitAsync(cancellationToken);
            await restore.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            Stop(dump);
            Stop(restore);
            dumpCall?.Failed(exception);
            restoreCall?.Failed(exception);
            throw;
        }

        dumpCall.Exited(dump.ExitCode, "(streamed to pg_restore)", await dumpErrors);
        var errors = await restoreErrors;
        restoreCall.Exited(restore.ExitCode, await restoreOutput, errors);
        if (dump.ExitCode != 0)
            throw new DatabaseCopyException($"pg_dump of {source.Display} failed (exit {dump.ExitCode}): {Tail(await dumpErrors)}");
        if (PgRestoreErrors.Fatal(errors) is { Count: > 0 } fatal)
            throw new DatabaseCopyException($"pg_restore into {temp} failed: {Tail(string.Join("\n", fatal))}");
        if (restore.ExitCode != 0 && !errors.Contains("pg_restore: error:", StringComparison.Ordinal))
            throw new DatabaseCopyException($"pg_restore into {temp} failed (exit {restore.ExitCode}): {Tail(errors)}");
    }

    static async Task PumpAsync(Process dump, Process restore, CancellationToken cancellationToken)
    {
        try
        {
            await dump.StandardOutput.BaseStream.CopyToAsync(restore.StandardInput.BaseStream, cancellationToken);
            restore.StandardInput.Close();
        }
        catch (IOException)
        {
            await dump.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        }
    }

    static IReadOnlyList<string> DockerRun(string name, string image, IReadOnlyDictionary<string, string> environment, bool stdin, IReadOnlyList<string> command) =>
    [
        "run", "--rm", "--name", name,
        .. stdin ? (string[])["-i"] : [],
        .. environment.Keys.SelectMany(variable => (string[])["-e", variable]),
        .. OperatingSystem.IsLinux() ? (string[])["--add-host", $"{PgEndpoint.DockerHost}:host-gateway"] : [],
        image,
        .. command,
    ];

    static Process Start(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, bool stdin)
    {
        try
        {
            return Process.Start(ProcessRunner.StartInfo("docker", arguments, null, stdin, environment)) ?? throw new DatabaseCopyException("could not start docker");
        }
        catch (Win32Exception exception)
        {
            throw new DatabaseCopyException($"could not run docker: {exception.Message}");
        }
    }

    static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
        }
    }

    static async Task ScrubAsync(PgEndpoint target, string temp, string path, CancellationToken cancellationToken)
    {
        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await using var connection = await OpenAsync(target, temp, false, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw new DatabaseCopyException($"scrub {Path.GetFileName(path)} failed and was rolled back: {exception.MessageText}");
        }
    }

    async Task MigrateAsync(PgEndpoint target, string temp, string project, InfraLog log, CancellationToken cancellationToken)
    {
        var connection = target.ConnectionString(temp);
        log.AddSecret(connection);
        log.Debug("db.copy.migrate", $"dotnet ef database update --project {project} --connection {connection}");
        await DotnetAsync("dotnet restore", ["restore", project], project, log, cancellationToken);
        await DotnetAsync("dotnet ef database update", ["ef", "database", "update", "--project", project, "--connection", connection], project, log, cancellationToken);
    }

    async Task DotnetAsync(string what, IReadOnlyList<string> arguments, string project, InfraLog log, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await runner.RunAsync("dotnet", arguments, Path.GetDirectoryName(project), null, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw new DatabaseCopyException($"could not run dotnet: {exception.Message}");
        }

        if (!result.Succeeded)
            throw new DatabaseCopyException($"{what} failed for {project}: {log.Redact(result.Tail())}");
    }

    async Task CleanUpAsync(PgEndpoint target, string temp, CopyContainers containers, InfraLog log)
    {
        foreach (var container in containers.All)
        {
            try
            {
                await runner.RunAsync("docker", ["rm", "-f", container], cancellationToken: CancellationToken.None);
            }
            catch (Win32Exception)
            {
            }
        }

        try
        {
            await using var admin = await OpenAsync(target, null, true, CancellationToken.None);
            await ExecuteAsync(admin, $"DROP DATABASE IF EXISTS {Quote(temp)} WITH (FORCE)", CancellationToken.None);
            log.Info("db.copy.cleanup", $"dropped {temp}");
        }
        catch (Exception exception) when (exception is NpgsqlException or DatabaseCopyException)
        {
            log.Warning("db.copy.cleanup", $"could not drop {temp} on {target.Host}:{target.Port}: {exception.Message}; drop it by hand");
        }
    }

    static async Task<NpgsqlConnection> OpenAsync(PgEndpoint endpoint, string? database, bool admin, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(endpoint.ConnectionString(database, admin));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (NpgsqlException exception)
        {
            await connection.DisposeAsync();
            throw new DatabaseCopyException(CopyRules.ConnectFailure(endpoint, admin ? endpoint.Admin.User : endpoint.Login.User, exception));
        }
    }

    static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw new DatabaseCopyException($"{sql} failed on {connection.Host}:{connection.Port}: {exception.MessageText}");
        }
    }

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 1500 ? trimmed : "..." + trimmed[^1500..];
    }
}