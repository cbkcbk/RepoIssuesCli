using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Repos;
using RepoInfraCli.Tests.Cloud;
using RepoInfraCli.Tests.Compose;
using RepoInfraCli.Tests.Hosts;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Repos;

public class RepoProviderTests
{
    const string Platform = """
                            local infra = require("@infra")
                            local compose = require("@infra/compose")
                            infra.project "platform" {}

                            local nas = infra.host "nas" { address = "10.0.0.50", user = "admin" }
                            local edge = compose.network "edge"

                            local function nodeExporter(h: infra.Host)
                                h:apt { packages = { "prometheus-node-exporter" } }
                                h:file "/etc/default/prometheus-node-exporter" { source = infra.file "files/node-exporter.conf" }
                            end

                            infra.play "nas" { hosts = { nas }, roles = { nodeExporter } }

                            return {
                                hosts = { nas = nas },
                                roles = { node_exporter = nodeExporter },
                                networks = { edge = edge },
                            }
                            """;

    const string Monitoring = """
                              local web = infra.host "web" { address = "10.0.0.1", user = "root" }
                              infra.play "monitoring" { hosts = { web }, roles = { platform.role "node_exporter" } }
                              """;

    TempRepo repo = null!;
    GitRemote remote = null!;
    string cacheRoot = "";
    string initial = "";

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        remote = new GitRemote();
        cacheRoot = Directory.CreateTempSubdirectory("infra-cache-").FullName;
        remote.WriteInfra(Platform)
            .Write("files/node-exporter.conf", "ARGS=--collector.systemd\n")
            .Write("src/app.txt", "application code the importer never needs\n");
        initial = remote.Commit("platform");
    }

    [TearDown]
    public void TearDown()
    {
        repo.Dispose();
        remote.Dispose();
        GitRemote.Delete(cacheRoot);
    }

    RepoCache Cache => new(cacheRoot);

    string Provider(string url, string reference = "main", string? path = null) =>
        $"local repo = require(\"@infra/repo\")\nlocal platform = repo.provider {{ url = \"{url}\", ref = \"{reference}\"{(path is null ? "" : $", path = \"{path}\"")} }}\n";

    EvaluationResult Evaluate(string body, string? reference = null, string? path = null, RepoCache? cache = null)
    {
        repo.WriteScript(Provider(remote.Url, reference ?? "main", path) + body);
        return InfraEvaluator.Evaluate(repo.Workspace, new EvaluationOptions { Repos = cache ?? Cache });
    }

    static string Describe(EvaluationResult result) => string.Join("\n", result.Diagnostics);

    [Test]
    public void SparseCloneEvaluatesTheOtherRepoAndRunsItsRoleOnALocalHost()
    {
        var result = Evaluate(Monitoring);

        Assert.That(result.Diagnostics, Is.Empty, Describe(result));
        var run = result.Graph!.Play("monitoring")!.Runs.Single();
        Assert.That(run.Role, Is.EqualTo("platform-infra/node_exporter"));
        Assert.That(run.External, Is.False);
        Assert.That(run.Host, Is.EqualTo(NodeId.Of("host", "web")));
        Assert.That(run.Tasks.Select(task => $"{task.Kind} {task.Host}"), Is.EqualTo(new[] { "apt host:web", "file host:web" }));
        Assert.That(run.Tasks[0].Location!.File, Is.EqualTo("platform-infra/.repo-infra/infra.luau"));

        var checkout = Cache.Folder(remote.Url);
        Assert.That(File.Exists(Path.Combine(checkout, ".repo-infra", "infra.luau")), Is.True);
        Assert.That(File.Exists(Path.Combine(checkout, "files", "node-exporter.conf")), Is.True, "files used by imported roles are fetched lazily");
        Assert.That(File.Exists(Path.Combine(checkout, "src", "app.txt")), Is.False, "the rest of the repo stays out of the sparse checkout");
        Assert.That(result.Graph.Hosts.Select(host => host.Name), Is.EqualTo(new[] { "web" }), "the other repo's plays don't run and its hosts aren't imported unless asked for");
    }

    [Test]
    public void ImportedRolePlansOnAFakeHost()
    {
        repo.WriteScript(Provider(remote.Url) + Monitoring);
        var connector = new FakeConnector().Host("web", host => host.On("dpkg-query", "pkg prometheus-node-exporter un \n"));

        var result = Plan(connector, "plan");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        var lines = result.Output.Split('\n').Select(line => line.TrimEnd()).ToList();
        Assert.That(lines, Does.Contain("      platform-infra/node_exporter")
            .And.Contain("        + apt: install prometheus-node-exporter")
            .And.Contain("        + file /etc/default/prometheus-node-exporter: create (25 bytes)"));
    }

    [Test]
    public void ImportedHostIsPlannedLikeALocalOne()
    {
        repo.WriteScript(Provider(remote.Url) + """
                                                infra.play "nas" { hosts = { platform.host "nas" }, roles = { function(h) h:apt { packages = { "curl" } } end } }
                                                """);
        var connector = new FakeConnector().Host("nas", host => host.On("dpkg-query", "pkg curl ii \n"));

        var result = Plan(connector, "plan");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("nas admin@10.0.0.50"));
        Assert.That(connector.Connects.Single().Options.Address, Is.EqualTo("10.0.0.50"));
    }

    [Test]
    public void CachedCopyIsReusedOfflineAndAMissingCacheIsAWarning()
    {
        var url = remote.Url;
        Assert.That(Evaluate(Monitoring).Diagnostics, Is.Empty);
        var marker = Path.Combine(Cache.Folder(url), ".git", "reused");
        File.WriteAllText(marker, "");
        remote.TakeOffline();

        repo.WriteScript(Provider(url) + Monitoring);
        var offline = InfraEvaluator.Evaluate(repo.Workspace, new EvaluationOptions { Repos = Cache });
        var uncached = InfraEvaluator.Evaluate(repo.Workspace, new EvaluationOptions { Repos = new RepoCache(Path.Combine(cacheRoot, "empty")) });

        Assert.That(offline.Diagnostics, Is.Empty, Describe(offline));
        Assert.That(offline.Graph!.Play("monitoring")!.Runs.Single().Tasks, Has.Count.EqualTo(2));
        Assert.That(File.Exists(marker), Is.True, "the cache folder was reused, not cloned again");

        var warning = uncached.Diagnostics.Single();
        Assert.That((warning.Severity, warning.Code), Is.EqualTo((Severity.Warning, DiagnosticCodes.RepoUnavailable)));
        Assert.That(warning.Message, Does.Contain("platform-infra").And.Contain("no cached copy").And.Contain("plan and apply fail"));
        Assert.That(uncached.Graph!.Play("monitoring")!.Runs.Single().External, Is.True);
    }

    [Test]
    public void OfflineCacheNeverClonesOrFetches()
    {
        const string body = """infra.play "nas" { hosts = { platform.host "nas" }, roles = { function(h) h:shell "true" end } }""";
        Assert.That(Evaluate(body).Errors, Is.Empty);
        remote.WriteInfra(Platform.Replace("10.0.0.50", "10.0.0.51"));
        remote.Commit("moved");

        var offline = Evaluate(body, cache: new RepoCache(cacheRoot) { Offline = true });
        var uncached = Evaluate(body, cache: new RepoCache(Path.Combine(cacheRoot, "empty")) { Offline = true });

        Assert.That(offline.Diagnostics, Is.Empty, Describe(offline));
        Assert.That(offline.Graph!.Hosts.Single().Address.Display(), Does.Contain("10.0.0.50"), "the new commit was not fetched");
        Assert.That(Directory.Exists(Path.Combine(cacheRoot, "empty")), Is.False, "nothing was cloned");
        Assert.That(uncached.Diagnostics.Single().Code, Is.EqualTo(DiagnosticCodes.RepoUnavailable));
        Assert.That(uncached.Diagnostics.Single().Message, Does.Contain("offline runs don't clone"));
        Assert.That(Evaluate(body).Graph!.Hosts.Single().Address.Display(), Does.Contain("10.0.0.51"), "an online run still fetches");
    }

    [Test]
    public void RefSelectsTagBranchOrCommit()
    {
        var first = initial;
        remote.Tag("v1");
        remote.WriteInfra(Platform.Replace("10.0.0.50", "10.0.0.51"));
        remote.Commit("v2");
        remote.Tag("v2");
        const string body = """infra.play "nas" { hosts = { platform.host "nas" }, roles = { function(h) h:shell "true" end } }""";

        string Address(string reference)
        {
            var result = Evaluate(body, reference);
            Assert.That(result.Errors, Is.Empty, Describe(result));
            return result.Graph!.Hosts.Single().Address.Display();
        }

        Assert.That(Address("v1"), Is.EqualTo("10.0.0.50"));
        Assert.That(Address("v2"), Is.EqualTo("10.0.0.51"));
        Assert.That(Address(first), Is.EqualTo("10.0.0.50"));
        Assert.That(Address("main"), Is.EqualTo("10.0.0.51"));
    }

    [Test]
    public void PathPointsAtTheInfraFolderInsideTheOtherRepo()
    {
        remote.WriteInfra(Platform.Replace("files/node-exporter.conf", "ops/files/node-exporter.conf"), "ops/infra")
            .Write("ops/files/node-exporter.conf", "ARGS=\n");
        remote.Commit("move infra");

        var result = Evaluate(Monitoring, path: "ops/infra");

        Assert.That(result.Diagnostics, Is.Empty, Describe(result));
        Assert.That(result.Graph!.Play("monitoring")!.Runs.Single().Tasks, Has.Count.EqualTo(2));
        Assert.That(File.Exists(Path.Combine(Cache.Folder(remote.Url), "ops", "infra", "infra.luau")), Is.True);
    }

    [Test]
    public void MissingExportsAreErrorsListingWhatTheRepoExports()
    {
        var result = Evaluate("""
                              platform.host "nope"
                              platform.role "nope"
                              platform.network "nope"
                              platform.database "nope"
                              """);

        var errors = result.Errors.ToList();
        Assert.That(errors.Select(error => error.Code), Is.All.EqualTo(DiagnosticCodes.MissingExport));
        Assert.That(errors.Select(error => error.Message), Is.EqualTo(new[]
        {
            "repo.provider platform-infra exports no host \"nope\"; its hosts: nas",
            "repo.provider platform-infra exports no role \"nope\"; its roles: node_exporter",
            "repo.provider platform-infra exports no network \"nope\"; its networks: edge",
            "repo.provider platform-infra exports no database \"nope\"; its databases: (none)",
        }));
        Assert.That(errors[0].Location, Is.EqualTo(new SourceLocation(".repo-infra/infra.luau", 5)));
    }

    static string ReExport(string url) => $$"""
                                            local infra = require("@infra")
                                            local repo = require("@infra/repo")
                                            infra.project "platform" {}
                                            local other = repo.provider { url = "{{url}}", ref = "main" }
                                            return { roles = { shared = other.role "shared" } }
                                            """;

    [Test]
    public void ProvidersImportingEachOtherAreACycle()
    {
        using var other = new GitRemote("other-infra");
        remote.WriteInfra(ReExport(other.Url));
        remote.Commit("import other");
        other.WriteInfra($$"""
                           local infra = require("@infra")
                           local repo = require("@infra/repo")
                           infra.project "other" {}
                           local platform = repo.provider { url = "{{remote.Url}}", ref = "main" }
                           return { roles = { shared = function(h) h:shell "true" end } }
                           """);
        other.Commit("import platform");

        var result = Evaluate(Monitoring.Replace("node_exporter", "shared"));

        var cycle = result.Errors.Single(error => error.Code == DiagnosticCodes.RepoCycle);
        Assert.That(cycle.Message, Does.Contain("repo.provider other-infra: repo.provider platform-infra:").And.Contain("is a cycle"));
    }

    [Test]
    public void RolesReExportedFromANestedProviderRunOnTheLocalHost()
    {
        using var other = new GitRemote("other-infra");
        other.WriteInfra("""
                         local infra = require("@infra")
                         infra.project "other" {}
                         return { roles = { shared = function(h) h:apt { packages = { "htop" } } end } }
                         """);
        other.Commit("shared");
        remote.WriteInfra(ReExport(other.Url));
        remote.Commit("re-export");

        var result = Evaluate(Monitoring.Replace("node_exporter", "shared"));

        Assert.That(result.Diagnostics, Is.Empty, Describe(result));
        var run = result.Graph!.Play("monitoring")!.Runs.Single();
        Assert.That(run.Role, Is.EqualTo("platform-infra/shared"));
        Assert.That(run.Tasks.Single().Options["packages"]!.Display(), Is.EqualTo("[htop]"));
        Assert.That(run.Tasks.Single().Host, Is.EqualTo(NodeId.Of("host", "web")));
    }

    [Test]
    public void ImportedDatabaseAndNetworkRenderInComposeAndAreLookups()
    {
        remote.WriteInfra("""
                          local infra = require("@infra")
                          local compose = require("@infra/compose")
                          local digitalocean = require("@infra/digitalocean")
                          infra.project "platform" {}
                          local cloud = digitalocean.provider { token = infra.secret "digitalocean/token" }
                          local main = cloud.database_cluster "main" { engine = "pg", version = "17", size = "db-s-1vcpu-1gb", region = "fra1" }
                          local reports = main.database "reports" { user = "reports" }
                          return { databases = { reports = reports }, networks = { edge = compose.network "edge" } }
                          """);
        remote.Commit("databases");

        var result = Evaluate("""
                              local reports = platform.database "reports"
                              local web = infra.host "web" { address = "10.0.0.1", user = "root" }
                              local function api(h)
                                  h:compose "api" { services = { api = { image = "nginx", networks = { platform.network "edge" }, environment = { Db = reports.connection_string } } } }
                              end
                              infra.play "apps" { hosts = { web }, roles = { api } }
                              """);

        Assert.That(result.Diagnostics, Is.Empty, Describe(result));
        var graph = result.Graph!;
        var database = graph.Resource(new NodeId("platform-infra/digitalocean.database:main/reports"))!;
        var cluster = graph.Resource(database.Parent!.Value)!;
        Assert.That(new[] { database.Reference, cluster.Reference }, Is.All.EqualTo(new Marker("platform", null)));
        Assert.That(graph.Provider(database.ProviderId)!.Reference, Is.EqualTo(new Marker("platform", null)));
        Assert.That(graph.Secrets.Select(secret => secret.Path), Does.Contain("digitalocean/token"));

        var task = graph.Play("apps")!.Runs.Single().Tasks.Single();
        var live = new StaticLiveValues(("platform-infra/digitalocean.database:main/reports", "connection_string", "postgresql://reports:pw@db:25060/reports"));
        var files = ComposeDocument.Render(task.Name!, task.Options, secret => secret.Path, live, strict: true);
        Assert.That(files.Networks, Is.EqualTo(new[] { "edge" }));
        Assert.That(files.Env, Does.Contain("API_DB='postgresql://reports:pw@db:25060/reports'"));
    }

    [Test]
    public void SelfHostedDatabasesCantBeImported()
    {
        remote.WriteInfra("""
                          local infra = require("@infra")
                          local compose = require("@infra/compose")
                          local postgres = require("@infra/postgres")
                          infra.project "platform" {}
                          local orders = postgres.database "orders" { version = "17", password = infra.secret "postgres/orders", network = compose.network "db", data = "/srv/orders" }
                          return { databases = { orders = orders } }
                          """);
        remote.Commit("self-hosted");

        var result = Evaluate("""platform.database "orders" """);

        Assert.That(result.Errors.Select(error => error.Message), Has.Some.Contains("\"orders\" is a self-hosted postgres.database"));
    }

    const string NasDroplet = """
                              local infra = require("@infra")
                              local digitalocean = require("@infra/digitalocean")
                              infra.project "platform" {}
                              local cloud = digitalocean.provider { token = infra.secret "digitalocean/token" }
                              local droplet = cloud.droplet "nas-1" { image = "ubuntu-24-04-x64", size = "s-1vcpu-1gb", region = "fra1" }
                              local nas = droplet:host { user = "root" }
                              return { hosts = { nas = nas } }
                              """;

    CloudHarness ImportNasDroplet()
    {
        remote.WriteInfra(NasDroplet);
        remote.Commit("droplet");
        var harness = new CloudHarness();
        harness.Script(Provider(remote.Url) + """
                                              infra.play "nas" { hosts = { platform.host "nas" }, roles = { function(h) h:apt { packages = { "curl" } } end } }
                                              """, prune: true);
        harness.EmptyAccount();
        return harness;
    }

    [Test]
    public void ImportedDropletIsLookedUpByTheOtherProjectsMarkerAndNeverChanged()
    {
        using var harness = ImportNasDroplet();
        harness.Account("droplets", "droplets", CloudHarness.Droplet("nas-1", tags: ["infra:platform"]));
        harness.Connector = new FakeConnector().Host("nas-1", host => host.On("dpkg-query", "pkg curl ii \n"));

        var result = harness.Run("apply", "--yes", "--allow-destroy");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("No changes."));
        Assert.That(harness.Http.Changes, Is.Empty);
        Assert.That(harness.Connector.Connects.Single().Options.Address, Is.EqualTo("203.0.113.10"));
    }

    [Test]
    public void ImportedDropletMissingUnderTheOtherProjectsMarkerIsAPlanProblem()
    {
        using var harness = ImportNasDroplet();
        harness.Account("droplets", "droplets", CloudHarness.Droplet("nas-1", tags: ["infra:someone-else"]));

        var result = harness.Run("plan");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid), result.Output + result.Errors);
        Assert.That(result.Output + result.Errors, Does.Contain("droplet \"nas-1\" with the marker infra:platform was not found"));
        Assert.That(harness.Http.Changes, Is.Empty);
    }

    RunResult Plan(IHostConnector connector, params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, services: services => services.RegisterInstance(connector)).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }
}