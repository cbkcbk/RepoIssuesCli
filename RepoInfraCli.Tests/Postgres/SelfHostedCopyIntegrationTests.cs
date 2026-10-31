using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Hosts;

namespace RepoInfraCli.Tests.Postgres;

[Category("Docker")]
[NonParallelizable]
public class SelfHostedCopyIntegrationTests
{
    const string Password = "self-hosted pw";

    INetwork network = null!;
    SshdContainer sshd = null!;
    IContainer server = null!;
    IContainer target = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        network = new NetworkBuilder().WithName($"infra-copy-test-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();
        server = new ContainerBuilder("postgres:17")
            .WithNetwork(network)
            .WithLabel(PostgresServices.ServerLabel, "shop")
            .WithEnvironment(new Dictionary<string, string> { ["POSTGRES_DB"] = "shop", ["POSTGRES_USER"] = "shop", ["POSTGRES_PASSWORD"] = Password })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(["pg_isready", "-h", "127.0.0.1", "-U", "shop", "-d", "shop"], wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        target = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_PASSWORD", Password)
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(["pg_isready", "-h", "127.0.0.1", "-U", "postgres"], wait => wait.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        var started = Task.WhenAll(server.StartAsync(), target.StartAsync());
        sshd = await SshdContainer.StartWithDockerAsync(network);
        await started;
        var seeded = await server.ExecAsync(["psql", "-U", "shop", "-d", "shop", "-v", "ON_ERROR_STOP=1", "-c", "create table items (id int primary key, name text); insert into items values (1, 'apple'), (2, 'pear');"]);
        Assert.That(seeded.ExitCode, Is.EqualTo(0), seeded.Stderr);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        await sshd.DisposeAsync();
        await server.DisposeAsync();
        await target.DisposeAsync();
        await network.DisposeAsync();
    }

    [Test]
    public async Task SelfHostedSourceIsCopiedThroughAnSshForwardToTheServerContainer()
    {
        using var repo = new TempRepo();
        repo.WriteScript($$"""
                           local compose = require("@infra/compose")
                           local postgres = require("@infra/postgres")
                           local net = compose.network "{{network.Name}}"
                           local shop = postgres.database "shop" { version = "17", password = infra.secret "pg/shop", network = net, data = "/srv/pg/shop" }
                           local box = infra.host "box" { address = "10.0.0.5", user = "nopw" }
                           local function data(h)
                             h:compose "data" { services = { pg = postgres.server(shop) } }
                           end
                           infra.play "p" { hosts = { box }, roles = { data } }
                           """);
        var evaluation = repo.Evaluate();
        Assert.That(evaluation.Diagnostics, Is.Empty, string.Join("\n", evaluation.Diagnostics));
        var graph = evaluation.Graph!;
        var log = InfraLog.Silent();
        var secrets = new SecretLookup(new EnvironmentSecretResolver(name => name == EnvironmentSecretResolver.VariableName("pg/shop") ? Password : null), repo.Workspace, null, log);
        var node = FakeHost.Node("box", become: true, user: "nopw", port: sshd.Port, address: new StringValue(sshd.Address));
        await using var connection = await sshd.Connector(sshd.KnownHostsFile("copy", sshd.HostKeyLine)).ConnectAsync(node, new HostConnectOptions { Address = sshd.Address });
        using var endpoints = new DatabaseEndpoints(graph, NoLiveValues.Instance, secrets, (_, _) => Task.FromResult(connection), log);

        var source = await endpoints.ResolveAsync(PostgresServices.DatabaseId("shop"), CancellationToken.None);
        var destination = new PgEndpoint(target.Hostname, target.GetMappedPublicPort(5432), "shop_copy", new PgLogin("postgres", Password));
        await new DatabaseCopier(ProcessRunner.Instance).CopyAsync(new CopyRequest(source, destination) { DeclaredMajor = 17 }, log);

        Assert.That(source, Has.Property(nameof(PgEndpoint.Host)).EqualTo("localhost").And.Property(nameof(PgEndpoint.Login)).EqualTo(new PgLogin("shop", Password)));
        await using var check = new NpgsqlConnection(destination.ConnectionString());
        await check.OpenAsync();
        await using var command = new NpgsqlCommand("select string_agg(name, ',' order by id) from items", check);
        Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("apple,pear"));
        Assert.That(log.Redact($"x{Password}x"), Does.Not.Contain(Password));
    }

    [Test]
    public async Task MissingServerContainerSaysToApplyItsPlayFirst()
    {
        var node = FakeHost.Node("box", become: true, user: "nopw", port: sshd.Port, address: new StringValue(sshd.Address));
        await using var connection = await sshd.Connector(sshd.KnownHostsFile("missing", sshd.HostKeyLine)).ConnectAsync(node, new HostConnectOptions { Address = sshd.Address });

        var failure = Assert.ThrowsAsync<DatabaseCopyException>(() => DatabaseEndpoints.ContainerAddressAsync(connection, "nothing", network.Name, CancellationToken.None));
        var wrongNetwork = Assert.ThrowsAsync<DatabaseCopyException>(() => DatabaseEndpoints.ContainerAddressAsync(connection, "shop", "elsewhere", CancellationToken.None));

        Assert.That(failure!.Message, Does.Contain("no running container on box has the label repo-infra.postgres.server=nothing"));
        Assert.That(wrongNetwork!.Message, Does.Contain("is not on the network elsewhere"));
    }
}
