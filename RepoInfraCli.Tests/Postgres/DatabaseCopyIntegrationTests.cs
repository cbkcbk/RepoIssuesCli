using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Postgres;

namespace RepoInfraCli.Tests.Postgres;

[Category("Docker")]
[NonParallelizable]
public class DatabaseCopyIntegrationTests
{
    const string Password = "copy-test pw'1";
    const int Port = 5432;

    IContainer source = null!;
    IContainer target = null!;
    string scratch = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        source = Postgres();
        target = Postgres();
        await Task.WhenAll(source.StartAsync(), target.StartAsync());
        await Sql(source, "postgres", "create database shop");
        await Sql(source, "shop", "create table items (id int primary key, name text not null, email text); insert into items select g, 'item ' || g, 'user' || g || '@example.com' from generate_series(1, 3) g;");
        scratch = Directory.CreateTempSubdirectory("infra-copy-tests-").FullName;
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        await source.DisposeAsync();
        await target.DisposeAsync();
        Directory.Delete(scratch, recursive: true);
    }

    static IContainer Postgres() =>
        new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_PASSWORD", Password)
            .WithPortBinding(Port, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(["pg_isready", "-h", "127.0.0.1", "-U", "postgres"], wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();

    static PgEndpoint Endpoint(IContainer container, string database) =>
        new(container.Hostname, container.GetMappedPublicPort(Port), database, new PgLogin("postgres", Password));

    static async Task<string> Sql(IContainer container, string database, string sql)
    {
        await using var connection = new NpgsqlConnection(Endpoint(container, database).ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString() ?? "";
    }

    async Task<string> CreateTargetAsync(string database)
    {
        await Sql(target, "postgres", $"create database {database}");
        await Sql(target, database, "create table old_stuff (id int); insert into old_stuff values (42);");
        return database;
    }

    Task<string> TempDatabases(string database) =>
        Sql(target, "postgres", $"select count(*) from pg_database where datname like '{database}\\_\\_infra\\_tmp\\_%'");

    static async Task<string> Containers(CopyContainers containers)
    {
        var result = await ProcessRunner.Instance.RunAsync("docker", ["ps", "-a", "-q", "--filter", $"name={containers.Dump}", "--filter", $"name={containers.Restore}"]);
        return result.Stdout.Trim();
    }

    string Scrub(string name, string sql)
    {
        var path = Path.Combine(scratch, name);
        File.WriteAllText(path, sql);
        return path;
    }

    static DatabaseCopier Copier() => new(ProcessRunner.Instance, FixedClock.Default);

    [Test]
    public async Task CopyReplacesTheTargetAndLeavesNoTemporaryDatabase()
    {
        var database = await CreateTargetAsync("plain");
        var log = InfraLog.Silent();
        var steps = new List<string>();

        await Copier().CopyAsync(new CopyRequest(Endpoint(source, "shop"), Endpoint(target, database)) { DeclaredMajor = 17 }, log, steps.Add);

        Assert.That(await Sql(target, database, "select string_agg(name, ',' order by id) from items"), Is.EqualTo("item 1,item 2,item 3"));
        Assert.That(await Sql(target, database, "select count(*) from pg_tables where tablename = 'old_stuff'"), Is.EqualTo("0"));
        Assert.That(await TempDatabases(database), Is.EqualTo("0"));
        Assert.That(steps, Has.Count.EqualTo(3).And.Some.StartsWith("pg_dump").And.Some.StartsWith("replace plain"));
    }

    [Test]
    public async Task ScrubRunsOnTheCopyAndNeverOnTheSource()
    {
        var database = await CreateTargetAsync("scrubbed");
        var scrub = Scrub("scrub.sql", "update items set email = 'redacted@example.com';\ndelete from items where id = 3;");

        await Copier().CopyAsync(new CopyRequest(Endpoint(source, "shop"), Endpoint(target, database)) { Scrub = scrub }, InfraLog.Silent());

        Assert.That(await Sql(target, database, "select string_agg(distinct email, ',') || ' ' || count(*) from items"), Is.EqualTo("redacted@example.com 2"));
        Assert.That(await Sql(source, "shop", "select count(*) from items where email like 'user%'"), Is.EqualTo("3"));
    }

    [Test]
    public async Task FailedScrubLeavesTheOldTargetIntact()
    {
        var database = await CreateTargetAsync("intact");
        var scrub = Scrub("broken.sql", "update items set email = null;\nupdate no_such_table set x = 1;");

        var failure = Assert.ThrowsAsync<DatabaseCopyException>(() =>
            Copier().CopyAsync(new CopyRequest(Endpoint(source, "shop"), Endpoint(target, database)) { Scrub = scrub }, InfraLog.Silent()));

        Assert.That(failure!.Message, Does.Contain("scrub broken.sql failed and was rolled back").And.Contain("no_such_table"));
        Assert.That(await Sql(target, database, "select string_agg(id::text, ',') from old_stuff"), Is.EqualTo("42"));
        Assert.That(await TempDatabases(database), Is.EqualTo("0"));
    }

    [Test]
    public async Task WrongDeclaredVersionRefusesBeforeCreatingAnything()
    {
        var database = await CreateTargetAsync("versioned");

        var failure = Assert.ThrowsAsync<DatabaseCopyException>(() =>
            Copier().CopyAsync(new CopyRequest(Endpoint(source, "shop"), Endpoint(target, database)) { DeclaredMajor = 16 }, InfraLog.Silent()));

        Assert.That(failure!.Message, Does.Contain("runs postgres 17 but is declared as version 16"));
        Assert.That(await TempDatabases(database), Is.EqualTo("0"));
    }

    [Test]
    public async Task CancellingRemovesTheContainersAndTheTemporaryDatabase()
    {
        await Sql(source, "postgres", "create database big");
        await Sql(source, "big", "create table rows as select g as id, repeat(md5(g::text), 20) as payload from generate_series(1, 400000) g");
        var database = await CreateTargetAsync("cancelled");
        var copier = Copier();
        using var cancel = new CancellationTokenSource();
        var containers = DatabaseCopier.Containers(copier.TempName(database));

        var copy = copier.CopyAsync(new CopyRequest(Endpoint(source, "big"), Endpoint(target, database)), InfraLog.Silent(), cancellationToken: cancel.Token);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while ((await Containers(containers)).Length == 0 && !copy.IsCompleted && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(100);
        Assert.That(copy.IsCompleted, Is.False, "the copy finished before it could be cancelled");
        await cancel.CancelAsync();

        Assert.That(async () => await copy, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await Containers(containers), Is.Empty);
        Assert.That(await TempDatabases(database), Is.EqualTo("0"));
        Assert.That(await Sql(target, database, "select count(*) from old_stuff"), Is.EqualTo("1"));
    }

    [Test]
    public async Task MigrationsRunOnTheCopyBeforeItReplacesTheTarget()
    {
        var ef = await ProcessRunner.Instance.RunAsync("dotnet", ["ef", "--version"]);
        Assume.That(ef.Succeeded, "the dotnet-ef tool is not installed");
        var database = await CreateTargetAsync("migrated");
        var project = MigrationsFixture.Write(Path.Combine(scratch, "widgets"));

        await Copier().CopyAsync(new CopyRequest(Endpoint(source, "shop"), Endpoint(target, database)) { MigrationsProject = project }, InfraLog.Silent());

        Assert.That(await Sql(target, database, "select count(*) from widgets"), Is.EqualTo("0"));
        Assert.That(await Sql(target, database, "select \"MigrationId\" from \"__EFMigrationsHistory\""), Is.EqualTo(MigrationsFixture.MigrationId));
        Assert.That(await Sql(target, database, "select count(*) from items"), Is.EqualTo("3"));
    }
}
