using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Compose;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Postgres;

[Category("Docker")]
[NonParallelizable]
public partial class BackupSidecarIntegrationTests
{
    const string S3Image = "chrislusf/seaweedfs:3.80";
    const int S3Port = 8333;
    const string AccessKey = "infra-test-access";
    const string SecretKey = "infra-test-secret-key";
    const string Bucket = "backups";
    const string Region = "ams3";

    const string Script = """
                          local digitalocean = require("@infra/digitalocean")
                          local compose = require("@infra/compose")
                          local postgres = require("@infra/postgres")
                          local secret = infra.secret
                          local cloud = digitalocean.provider { token = secret "do/token", spaces_access_key = secret "do/ak", spaces_secret_key = secret "do/sk" }
                          local bucket = cloud.spaces_bucket "backups" { region = "ams3", lifecycle = { { prefix = "db/", expire_days = 7 } } }
                          local net = compose.network "db"
                          local shop = postgres.database "shop" { version = "17", password = secret "pg/shop", network = net, data = "/srv/pg/shop", user = "owner" }
                          local box = infra.host "box" { address = "10.0.0.5", user = "root" }

                          local function data(h)
                            h:compose "data" {
                              services = {
                                pg = postgres.server(shop),
                                backup = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "* * * * *", prefix = "db/shop" },
                              },
                            }
                          end
                          infra.play "p" { hosts = { box }, roles = { data } }
                          """;

    static readonly Dictionary<string, string> Secrets = new()
    {
        ["pg/shop"] = "s3cr3t pg'pw",
        ["spaces/backups"] = $"{AccessKey}:{SecretKey}",
    };

    readonly ConcurrentBag<string> manualKeys = [];

    TempRepo repo = null!;
    INetwork network = null!;
    IContainer storage = null!;
    IContainer server = null!;
    IContainer sidecar = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        repo = new TempRepo();
        repo.WriteScript(Script);
        var evaluation = repo.Evaluate();
        Assert.That(evaluation.Diagnostics, Is.Empty, string.Join("\n", evaluation.Diagnostics));
        var graph = evaluation.Graph!;
        var lookup = new SecretLookup(new EnvironmentSecretResolver(name => Secrets.FirstOrDefault(pair => EnvironmentSecretResolver.VariableName(pair.Key) == name).Value),
            repo.Workspace, null, new SecretRedactor());
        var live = new SelfHostedDatabases(new StaticLiveValues(("digitalocean.spaces_bucket:backups", "endpoint", $"http://s3:{S3Port}")), graph, lookup);
        var compose = graph.Tasks.Single(task => task.Kind == TaskKinds.Compose);
        var services = (MapValue)compose.Options["services"]!;
        var script = graph.Tasks.Single(task => task.Kind == TaskKinds.File && task.Name == BackupScript.HostPath).Options.GetString("content")!;

        string Resolve(InfraValue value) => value switch
        {
            StringValue text => text.Value,
            SecretRef secret => Secrets[secret.Path],
            OutputRef output => (live.Resolve(output) as StringValue)?.Value ?? throw new AssertionException($"{output} has no value"),
            _ => throw new AssertionException($"unexpected {value}"),
        };

        IReadOnlyDictionary<string, string> Environment(string service) =>
            ((MapValue)((MapValue)services[service]!)["environment"]!).Entries.ToDictionary(entry => entry.Key, entry => Resolve(entry.Value));

        network = new NetworkBuilder().WithName($"infra-backup-test-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();

        storage = new ContainerBuilder(S3Image)
            .WithNetwork(network)
            .WithNetworkAliases("s3")
            .WithPortBinding(S3Port, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(S3Config), "/etc/s3.json")
            .WithCommand("server", "-dir=/data", "-s3", $"-s3.port={S3Port}", "-s3.config=/etc/s3.json")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(S3Port))
            .Build();
        var pg = (MapValue)services["pg"]!;
        server = new ContainerBuilder(pg.GetString("image")!)
            .WithNetwork(network)
            .WithNetworkAliases("pg")
            .WithEnvironment(Environment("pg"))
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(["pg_isready", "-h", "127.0.0.1", "-U", "owner", "-d", "shop"], wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        await Task.WhenAll(storage.StartAsync(), server.StartAsync());
        await CreateBucketAsync();

        var backup = (MapValue)services["backup"]!;
        var builder = new ContainerBuilder(backup.GetString("image")!)
            .WithNetwork(network)
            .WithEnvironment(Environment("backup"))
            .WithCommand([.. ((ListValue)backup["command"]!).Items.Select(Resolve)])
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(["test", "-f", BackupScript.ReadyFile], wait => wait.WithTimeout(TimeSpan.FromMinutes(4))));
        foreach (var mount in ((ListValue)backup["volumes"]!).Items.Select(Resolve))
        {
            var parts = mount.Split(':');
            Assert.That(parts[0], Is.EqualTo(BackupScript.HostPath));
            builder = builder.WithResourceMapping(Encoding.UTF8.GetBytes(script), parts[1], fileMode: Executable);
        }
        sidecar = builder.Build();
        await sidecar.StartAsync();

        await Sql("create table items (id int primary key, name text); insert into items values (1, 'apple'), (2, 'pear');");
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        await sidecar.DisposeAsync();
        await server.DisposeAsync();
        await storage.DisposeAsync();
        await network.DisposeAsync();
        repo.Dispose();
    }

    const UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
                                     | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    static string S3Config => $$"""
                                {"identities":[{"name":"test","credentials":[{"accessKey":"{{AccessKey}}","secretKey":"{{SecretKey}}"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
                                """;

    async Task CreateBucketAsync()
    {
        using var http = new HttpClient();
        var uri = new Uri($"http://{storage.Hostname}:{storage.GetMappedPublicPort(S3Port)}/{Bucket}");
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, uri);
            SigV4.Sign(request, [], Region, AccessKey, SecretKey, DateTimeOffset.UtcNow);
            try
            {
                using var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return;
                if (attempt >= 60)
                    Assert.Fail($"creating bucket {Bucket} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
            catch (HttpRequestException) when (attempt < 60)
            {
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    async Task<string> Sql(string sql, string database = "shop")
    {
        var result = await server.ExecAsync(["psql", "-U", "owner", "-d", database, "-v", "ON_ERROR_STOP=1", "-tAc", sql]);
        Assert.That(result.ExitCode, Is.EqualTo(0), result.Stderr);
        return result.Stdout.Trim();
    }

    async Task<ExecResult> Sidecar(params string[] command) => await sidecar.ExecAsync(command);

    async Task<IReadOnlyList<string[]>> ListAsync()
    {
        var listed = await Sidecar("list");
        Assert.That(listed.ExitCode, Is.EqualTo(0), listed.Stderr);
        return [.. listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))];
    }

    [Test]
    public async Task BackupListAndRestoreRoundTripThroughS3()
    {
        var backup = await Sidecar("backup");
        Assert.That(backup.ExitCode, Is.EqualTo(0), backup.Stderr);
        var key = backup.Stdout.Trim();
        manualKeys.Add(key);
        Assert.That(key, Does.Match(@"^db/shop/shop-\d{8}T\d{6}Z\.dump$"));
        Assert.That(backup.Stdout + backup.Stderr, Does.Not.Contain(SecretKey).And.Not.Contain("s3cr3t"));

        var listed = (await ListAsync()).Single(entry => entry[0] == key);
        Assert.That(long.Parse(listed[1]), Is.GreaterThan(0));
        Assert.That(DateTimeOffset.Parse(listed[2]), Is.EqualTo(DateTimeOffset.UtcNow).Within(TimeSpan.FromMinutes(5)));

        await Sql("delete from items; insert into items values (3, 'plum');");
        var restored = await Sidecar("restore", key);
        Assert.That(restored.ExitCode, Is.EqualTo(0), restored.Stderr);
        Assert.That(await Sql("select string_agg(name, ',' order by id) from items"), Is.EqualTo("apple,pear"));

        var drill = await Sidecar("restore", key, "shop_drill");
        Assert.That(drill.ExitCode, Is.EqualTo(0), drill.Stderr);
        Assert.That(await Sql("select count(*) from items", "shop_drill"), Is.EqualTo("2"));
    }

    [Test]
    public async Task ScheduledBackupsRunOnTheCronSchedule()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(150);
        while (!(await ListAsync()).Any(entry => !manualKeys.Contains(entry[0])))
        {
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"no scheduled backup appeared; sidecar logs:\n{(await sidecar.GetLogsAsync()).Stderr}");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        var leftovers = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            leftovers = (await Sidecar("find", "/tmp", "-name", "infra-backup.??????")).Stdout;
            if (leftovers.Length == 0)
                break;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        Assert.That(leftovers, Is.Empty);
    }

    [Test]
    public async Task FailuresSayWhatIsWrong()
    {
        var missing = await Sidecar("restore", "db/shop/shop-19700101T000000Z.dump");
        var malformed = await Sidecar("env", "BACKUP_CREDENTIALS=no-separator", "backup");
        var unknown = await Sidecar("env", "BACKUP_PREFIX=elsewhere", "list");
        var forged = await Sidecar("env", $"BACKUP_CREDENTIALS={AccessKey}:wrong-secret", "backup");

        Assert.That(missing.ExitCode, Is.Not.EqualTo(0));
        Assert.That(missing.Stderr, Does.Contain("downloading db/shop/shop-19700101T000000Z.dump"));
        Assert.That(malformed.ExitCode, Is.Not.EqualTo(0));
        Assert.That(malformed.Stderr, Does.Contain("BACKUP_CREDENTIALS must be <access key>:<secret key>"));
        Assert.That(unknown.ExitCode, Is.EqualTo(0), unknown.Stderr);
        Assert.That(unknown.Stdout, Is.Empty);
        Assert.That(forged.ExitCode, Is.Not.EqualTo(0));
        Assert.That(forged.Stderr, Does.Contain("403").And.Not.Contain("wrong-secret"));
    }

    [Test]
    public async Task RestoreCommandListsAndRestoresThroughTheSidecar()
    {
        var before = await Sql("select string_agg(name, ',' order by id) from items");
        var backup = await Sidecar("backup");
        Assert.That(backup.ExitCode, Is.EqualTo(0), backup.Stderr);
        var key = backup.Stdout.Trim();
        manualKeys.Add(key);
        var connector = new SidecarConnector(sidecar);

        var listed = Infra(connector, "restore", "shop", "--list", "--json");
        await Sql("delete from items; insert into items values (9, 'fig');");
        var drill = Infra(connector, "restore", "shop", "--dump", key, "--into", "shop_cli", "--yes");
        var live = Infra(connector, "restore", "shop", "--dump", key, "--yes");

        Assert.That(listed.ExitCode, Is.Zero, listed.Errors);
        Assert.That(listed.Output, Does.Contain($"\"key\": \"{key}\""));
        Assert.That(drill.ExitCode, Is.Zero, drill.Output + drill.Errors);
        Assert.That(await Sql("select string_agg(name, ',' order by id) from items", "shop_cli"), Is.EqualTo(before));
        Assert.That(live.ExitCode, Is.Zero, live.Output + live.Errors);
        Assert.That(live.Output, Does.Contain($"Restored {key} into shop."));
        Assert.That(await Sql("select string_agg(name, ',' order by id) from items"), Is.EqualTo(before));
        Assert.That(connector.Scripts, Has.All.Contains("docker compose -p 'data' run --rm -T 'backup'"));
    }

    RunResult Infra(IHostConnector connector, params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, services: services => services.RegisterInstance(connector)).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    sealed partial class SidecarConnector(IContainer sidecar) : IHostConnector, IHostConnection
    {
        HostNode? connected;

        public ConcurrentQueue<string> Scripts { get; } = new();

        public HostNode Host => connected!;

        public HostFacts Facts { get; } = new("ubuntu", "24.04", "noble");

        public Task<IHostConnection> ConnectAsync(HostNode host, HostConnectOptions options, CancellationToken cancellationToken = default)
        {
            connected = host;
            return Task.FromResult<IHostConnection>(this);
        }

        public async Task<CommandResult> RunAsync(string script, ReadOnlyMemory<byte> stdin = default, CancellationToken cancellationToken = default)
        {
            Scripts.Enqueue(script);
            var run = script.ReplaceLineEndings("\n").Split('\n').Single(line => line.StartsWith("docker compose ", StringComparison.Ordinal));
            var arguments = Quoted().Matches(run[run.IndexOf(" run ", StringComparison.Ordinal)..]).Select(match => match.Groups[1].Value).Skip(1).ToList();
            var result = await sidecar.ExecAsync(arguments, cancellationToken);
            return new CommandResult((int)(result.ExitCode ?? -1),result.Stdout, result.Stderr);
        }

        public Task UploadAsync(string path, ReadOnlyMemory<byte> content, string? mode, string? owner, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        [GeneratedRegex("'([^']*)'")]
        private static partial Regex Quoted();
    }
}
