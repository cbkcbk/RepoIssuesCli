using System.Text.Json;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Tests.Hosts;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Postgres;

public class RestoreCommandTests
{
    const string WebAddress = "203.0.113.10";
    const string Oldest = "postgres/api/api-20261028T030000Z.dump";
    const string Middle = "postgres/api/api-20261029T030000Z.dump";
    const string Newest = "postgres/api/api-20261030T030000Z.dump";
    const string ListCommand = "run --rm -T 'backup' 'list'";
    const string RestoreFragment = "run --rm -T 'backup' 'restore'";

    static readonly string Listing = string.Join('\n',
        $"{Middle}\t2048\t2026-10-29T03:00:09.000Z",
        $"{Newest}\t3145728\t2026-10-30T03:00:11.000Z",
        $"{Oldest}\t512\t2026-10-28T03:00:07.000Z") + "\n";

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.CopyDemo();
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    static FakeConnector Connector(Action<FakeHost>? setup = null) => new FakeConnector().Host("web-1", host =>
    {
        host.On(ListCommand, Listing);
        setup?.Invoke(host);
    });

    RunResult Run(IHostConnector connector, params string[] args) => Run(connector, new TestConsole().Width(250), new DropletAddress(), args);

    RunResult Run(IHostConnector connector, TestConsole output, ILiveValues live, params string[] args)
    {
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, services: services =>
        {
            services.RegisterInstance(connector);
            services.RegisterInstance(live);
            services.RegisterInstance<IResourcePlanner>(NoResourcePlanner.Instance);
        }).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    static IReadOnlyList<string> Restores(FakeConnector connector) =>
        connector.Opened.TryGetValue("web-1", out var host) ? [.. host.Scripts.Where(script => script.Contains(RestoreFragment, StringComparison.Ordinal))] : [];

    sealed class DropletAddress : ILiveValues
    {
        public InfraValue? Resolve(OutputRef output) => output.Field == "ipv4_address" ? new StringValue(WebAddress) : null;

        public bool CreatedThisRun(NodeId resource) => false;
    }

    [Test]
    public void ListRunsTheSidecarOnTheBackupHostAndPrintsNewestFirst()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api", "--list");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(result.Output.IndexOf(Newest, StringComparison.Ordinal), Is.LessThan(result.Output.IndexOf(Middle, StringComparison.Ordinal)));
        Assert.That(result.Output.IndexOf(Middle, StringComparison.Ordinal), Is.LessThan(result.Output.IndexOf(Oldest, StringComparison.Ordinal)));
        Assert.That(result.Output, Does.Contain("2026-10-30 03:00:00 UTC").And.Contain("3.0 MB").And.Contain("512 B"));
        Assert.That(connector.Connects.Single().Options.Address, Is.EqualTo(WebAddress));
        var script = connector.Opened["web-1"].Scripts.Single();
        Assert.That(script, Does.Contain("d='/opt/postgres'").And.Contain("cd \"$d\"").And.Contain($"docker compose -p 'postgres' {ListCommand}"));
        Assert.That(connector.Opened["web-1"].Disposed, Is.True);
    }

    [Test]
    public void ListAsJsonHasTheDumps()
    {
        var result = Run(Connector(), "restore", "api", "--list", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(json.GetProperty("database").GetString(), Is.EqualTo("api"));
        Assert.That(json.GetProperty("host").GetString(), Is.EqualTo("web-1"));
        Assert.That(json.GetProperty("location").GetString(), Does.EndWith("postgres/api/"));
        Assert.That(json.GetProperty("dumps").EnumerateArray().Select(dump => dump.GetProperty("key").GetString()), Is.EqualTo(new[] { Newest, Middle, Oldest }));
        Assert.That(json.GetProperty("dumps")[0].GetProperty("size").GetInt64(), Is.EqualTo(3145728));
    }

    [Test]
    public void DefaultRestoresTheNewestDumpOverTheLiveDatabaseAndWarnsAboutApps()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api", "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(Restores(connector).Single().TrimEnd(), Does.EndWith($"{RestoreFragment} '{Newest}'"));
        Assert.That(result.Output, Does.Contain("Restore postgres database api").And.Contain(Newest).And.Contain("3.0 MB")
            .And.Contain($"root@{WebAddress} web-1 (play data > database, compose postgres service backup)")
            .And.Contain("the live database api"));
        Assert.That(result.Output, Does.Contain("Warning:").And.Contain("app(s) api use api"));
        Assert.That(result.Output, Does.Contain($"Restored {Newest} into api."));
    }

    [Test]
    public void AtPicksTheNewestDumpAtOrBeforeThatTime()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api", "--at", "2026-10-29T12:00Z", "--yes");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        Assert.That(Restores(connector).Single(), Does.Contain($"'{Middle}'"));
    }

    [Test]
    public void AtWithoutOffsetIsUtcAndIncludesADumpTakenExactlyThen()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api", "--at", "2026-10-28 03:00:00", "--yes");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        Assert.That(Restores(connector).Single(), Does.Contain($"'{Oldest}'"));
    }

    [Test]
    public void AtBeforeEveryDumpIsNotFoundAndNamesTheOldest()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api", "--at", "2026-10-01", "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(result.Errors, Does.Contain("No dump of api at or before 2026-10-01 00:00:00 UTC").And.Contain(Oldest));
        Assert.That(Restores(connector), Is.Empty);
    }

    [Test]
    public void DumpPicksThatKeyAndAnUnknownKeyIsNotFound()
    {
        var picked = Connector();
        var unknown = Connector();

        var exact = Run(picked, "restore", "api", "--dump", Oldest, "--yes");
        var missing = Run(unknown, "restore", "api", "--dump", "postgres/api/api-19700101T000000Z.dump", "--yes");

        Assert.That(exact.ExitCode, Is.Zero, exact.Output + exact.Errors);
        Assert.That(Restores(picked).Single(), Does.Contain($"'{Oldest}'"));
        Assert.That(missing.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(missing.Errors, Does.Contain("No dump 'postgres/api/api-19700101T000000Z.dump' of api").And.Contain("infra restore api --list"));
        Assert.That(Restores(unknown), Is.Empty);
    }

    [Test]
    public void NoDumpsYetIsNotFound()
    {
        var connector = new FakeConnector().Host("web-1", host => host.On(ListCommand, ""));

        var result = Run(connector, "restore", "api", "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(result.Errors, Does.Contain("No dumps of api"));
    }

    [Test]
    public void IntoRestoresIntoANewDatabaseWithoutTheAppWarning()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api", "--into", "api_drill", "--yes");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        Assert.That(Restores(connector).Single(), Does.Contain($"{RestoreFragment} '{Newest}' 'api_drill'"));
        Assert.That(result.Output, Does.Contain("the new database api_drill").And.Not.Contain("Warning:"));
    }

    [Test]
    public void WithoutATerminalOrYesItIsAUsageErrorBeforeConnecting()
    {
        var connector = Connector();

        var result = Run(connector, "restore", "api");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--yes"));
        Assert.That(connector.Connects, Is.Empty);
    }

    [Test]
    public void InteractiveRestoreNeedsTheDatabaseNameTyped()
    {
        var refused = Connector();
        var typedWrong = new TestConsole().Width(250).Interactive();
        typedWrong.Input.PushTextWithEnter("y");
        var accepted = Connector();
        var typedRight = new TestConsole().Width(250).Interactive();
        typedRight.Input.PushTextWithEnter("api");

        var cancelled = Run(refused, typedWrong, new DropletAddress(), "restore", "api");
        var restored = Run(accepted, typedRight, new DropletAddress(), "restore", "api");

        Assert.That(cancelled.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(cancelled.Output, Does.Contain("Type api to restore:").And.Contain("Restore cancelled."));
        Assert.That(Restores(refused), Is.Empty);
        Assert.That(restored.ExitCode, Is.Zero, restored.Output + restored.Errors);
        Assert.That(Restores(accepted), Has.Count.EqualTo(1));
    }

    [TestCase("reports")]
    [TestCase("crm")]
    public void ManagedDatabasesAreRefusedPointingAtDigitalOceanAndDbCopy(string database)
    {
        var connector = Connector();

        var result = Run(connector, "restore", database, "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Errors, Does.Contain("managed DigitalOcean database").And.Contain("DigitalOcean backs up and restores").And.Contain($"infra db copy {database}"));
        Assert.That(connector.Connects, Is.Empty);
    }

    [Test]
    public void UnknownDatabaseIsNotFoundAndListsTheBackedUpOnes()
    {
        var result = Run(Connector(), "restore", "nope", "--list");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(result.Errors, Does.Contain("No database named 'nope'. Databases with backups: api."));
    }

    [TestCase("--dump", "k", "--at", "2026-10-29", "Use --dump or --at, not both.")]
    [TestCase("--list", "--into", "x", "", "--list can't be combined")]
    [TestCase("--into", "api", "", "", "leave --into out")]
    [TestCase("--into", "bad name", "", "", "must start with a letter")]
    [TestCase("--at", "yesterday-ish", "", "", "is not a time")]
    public void BadOptionCombinationsAreUsageErrors(string first, string second, string third, string fourth, string message)
    {
        var connector = Connector();

        var result = Run(connector, ["restore", "api", .. new[] { first, second, third, fourth }.Where(arg => arg.Length > 0), "--yes"]);

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage), result.Errors);
        Assert.That(result.Errors, Does.Contain(message));
        Assert.That(connector.Connects, Is.Empty);
    }

    [Test]
    public void SidecarFailuresAreReportedWithTheirOutput()
    {
        var connector = Connector(host => host.On(RestoreFragment, "", exitCode: 1, stderr: "2026-10-31T00:00:00Z error: restore into api failed"));

        var result = Run(connector, "restore", "api", "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Errors, Does.Contain("restoring " + Newest + " into api failed with exit code 1").And.Contain("error: restore into api failed"));
    }

    [Test]
    public void ConnectionUsesAcceptNewHostsAndFailuresExitOne()
    {
        var accepting = Connector();
        var failing = Connector();
        failing.Fail("web-1", "host key for web-1 is not in known_hosts");

        Run(accepting, "restore", "api", "--list", "--accept-new-hosts");
        var failed = Run(failing, "restore", "api", "--list");

        Assert.That(accepting.Connects.Single().Options.AcceptNewHosts, Is.True);
        Assert.That(accepting.Connects.Single().Options.TrustOnFirstUse, Is.False);
        Assert.That(failed.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(failed.Errors, Does.Contain("not in known_hosts"));
    }

    [Test]
    public void AnUnknownHostAddressIsReportedWithoutConnecting()
    {
        var connector = Connector();

        var result = Run(connector, new TestConsole().Width(250), NoLiveValues.Instance, "restore", "api", "--list");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Errors, Does.Contain("the address of host 'web-1' is not known"));
        Assert.That(connector.Connects, Is.Empty);
    }

    [Test]
    public void DatabaseWithoutBackupIsNotFoundAndExternalDatabasesAreRefused()
    {
        repo.WriteScript("""
                         local postgres = require("@infra/postgres")
                         local compose = require("@infra/compose")
                         local repo = require("@infra/repo")
                         local platform = repo.provider { url = "git@github.com:snappy/platform-infra.git", ref = "v1" }
                         local orders = platform.database "orders"
                         local shop = postgres.database "shop" { version = "17", password = infra.secret "pg/shop", network = compose.network "db", data = "/srv/pg/shop" }
                         local box = infra.host "box" { address = "10.0.0.5", user = "root" }
                         infra.play "p" { hosts = { box }, roles = { function(h)
                           h:compose "data" { services = { pg = postgres.server(shop) } }
                           h:compose "app" { services = { app = { image = "nginx", environment = { A = shop.connection_string, B = orders.connection_string } } } }
                         end } }
                         """);
        var connector = new FakeConnector();

        var noBackup = Run(connector, "restore", "shop", "--list");
        var external = Run(connector, "restore", "orders", "--list");

        Assert.That(noBackup.ExitCode, Is.EqualTo(InfraExitCodes.NotFound), noBackup.Output + noBackup.Errors);
        Assert.That(noBackup.Errors, Does.Contain("postgres.database \"shop\" has no postgres.backup"));
        Assert.That(external.ExitCode, Is.EqualTo(InfraExitCodes.Invalid), external.Output + external.Errors);
        Assert.That(external.Errors, Does.Contain("declared in another repo").And.Contain("infra db copy orders"));
        Assert.That(connector.Connects, Is.Empty);
    }
}
