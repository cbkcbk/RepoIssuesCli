using System.Text;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Hosts;
using static RepoInfraCli.Tests.Hosts.Options;

namespace RepoInfraCli.Tests.Compose;

public class ComposeTaskTests
{
    const string Token = "tok-123-secret";
    const string Image = "registry.example/app/api:abc";

    static readonly InfraWorkspace Workspace = new(Path.GetTempPath());

    static readonly ComposeTask Module = new(new ComposeWait(TimeSpan.Zero, TimeSpan.Zero));

    static readonly SecretLookup Secrets = new(
        new EnvironmentSecretResolver(name => name == EnvironmentSecretResolver.VariableName("api/token") ? Token : null), Workspace, null, new SecretRedactor());

    static readonly StaticLiveValues Live = new(("dotnet.container:Api", "image", Image));

    static TaskNode ApiTask(InfraValue? image = null) => Task("compose", "api", Map(
        ("dir", Text("/srv/api")),
        ("services", Map(("api", Map(
            ("image", image ?? new OutputRef(new NodeId("dotnet.container:Api"), "image")),
            ("networks", new ListValue([new NodeRef(new NodeId("compose.network:edge"))])),
            ("environment", Map(("TOKEN", new SecretRef("api/token")), ("MODE", Text("prod")))),
            ("healthcheck", Map(("http", Text("http://localhost:8080/health"))))))))));

    static TaskContext Context(FakeHost host, TaskNode task, ILiveValues? live = null, SecretLookup? secrets = null) =>
        new(host, task, Workspace, live ?? Live, secrets ?? Secrets);

    static ComposeFiles Rendered(TaskNode task) => ComposeTask.Render(Context(new FakeHost(FakeHost.Node()), task), strict: true);

    static string Hash(string text) => FileTask.Sha256(Encoding.UTF8.GetBytes(text));

    static string[] Lines(TaskProbe probe) => [.. probe.Changes.Select(change => $"{change.Marker.Symbol()} {change.Text}")];

    static FakeHost Converged(ComposeFiles files, string runningImage = Image, string envMode = "600") =>
        new FakeHost(FakeHost.Node()).On("sha256sum", $"""
            file compose.yaml {Hash(files.Yaml)} 644
            file .env {Hash(files.Env)} {envMode}
            docker
            network edge
            service api {runningImage} running

            """);

    [Test]
    public async Task FreshHostCreatesFilesAndNetwork()
    {
        var host = new FakeHost(FakeHost.Node()).On("sha256sum", "docker\n");

        var probe = await Module.ProbeAsync(Context(host, ApiTask()), CancellationToken.None);

        Assert.That(Lines(probe), Is.EqualTo(new[]
        {
            "+ create /srv/api/compose.yaml and .env (service(s) api; network(s) edge; 1 secret(s) (secret))",
            "+ network edge (external)",
        }));
        Assert.That(probe.HasDrift, Is.True);
    }

    [Test]
    public async Task ConvergedHostHasNoChanges()
    {
        var task = ApiTask();
        var host = Converged(Rendered(task));

        var probe = await Module.ProbeAsync(Context(host, task), CancellationToken.None);

        Assert.That(probe.Changes, Is.Empty);
    }

    [Test]
    public async Task DifferentRunningTagRedeploysEvenWithSameFiles()
    {
        var task = ApiTask();
        var host = Converged(Rendered(task), runningImage: "registry.example/app/api:old");

        var probe = await Module.ProbeAsync(Context(host, task), CancellationToken.None);

        Assert.That(Lines(probe), Is.EqualTo(new[] { "~ service api: running registry.example/app/api:old -> registry.example/app/api:abc" }));
    }

    [Test]
    public async Task ChangedFilesAreSummarisedWithoutPrintingSecrets()
    {
        var task = ApiTask();
        var desired = Rendered(task);
        var oldYaml = desired.Yaml.Replace("api:abc", "api:old").Replace("\"prod\"", "\"dev\"");
        var oldEnv = desired.Env.Replace(Token, "previous-token");
        var host = new FakeHost(FakeHost.Node()).On("sha256sum", $"file compose.yaml {Hash(oldYaml)} 644\nfile .env {Hash(oldEnv)} 644\ndocker\nnetwork edge\n");
        host.Files["/srv/api/compose.yaml"] = Encoding.UTF8.GetBytes(oldYaml);
        host.Files["/srv/api/.env"] = Encoding.UTF8.GetBytes(oldEnv);

        var probe = await Module.ProbeAsync(Context(host, task), CancellationToken.None);
        var lines = Lines(probe);

        Assert.That(lines, Is.EqualTo(new[]
        {
            "~ service api: image registry.example/app/api:old -> registry.example/app/api:abc; environment changed",
            "~ .env: ~API_TOKEN (secret)",
            "~ .env mode 644 -> 0600",
        }));
        Assert.That(string.Join("\n", lines), Does.Not.Contain(Token).And.Not.Contain("previous-token"));
    }

    [Test]
    public async Task UnknownOutputsShowAsKnownAfterApplyAndBlockApply()
    {
        var task = ApiTask();
        var host = new FakeHost(FakeHost.Node()).On("sha256sum", "docker\nnetwork edge\n");

        var probe = await Module.ProbeAsync(Context(host, task, live: NoLiveValues.Instance), CancellationToken.None);
        var error = Assert.ThrowsAsync<TaskPlanException>(() => Module.ApplyAsync(Context(host, task, live: NoLiveValues.Instance), probe, CancellationToken.None));

        Assert.That(Lines(probe).Single(), Does.EndWith("; (known after apply): api.image"));
        Assert.That(error!.Message, Does.Contain("api.image").And.Contain("no value yet"));
        Assert.That(host.Uploads, Is.Empty);
    }

    [Test]
    public void MissingSecretFailsThePlanWithTheFix()
    {
        var host = new FakeHost(FakeHost.Node());

        var error = Assert.ThrowsAsync<SecretUnavailableException>(() => Module.ProbeAsync(Context(host, ApiTask(), secrets: SecretLookup.Unavailable(Workspace)), CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("api/token"));
    }

    [Test]
    public async Task ApplyWritesFilesCreatesNetworksRunsUpAndWaitsForHttpHealth()
    {
        var task = ApiTask();
        var host = new FakeHost(FakeHost.Node())
            .On("sha256sum", "docker\n")
            .On("docker inspect", "api running none 172.18.0.5 \n")
            .On("curl", "ok\n");
        var probe = await Module.ProbeAsync(Context(host, task), CancellationToken.None);

        await Module.ApplyAsync(Context(host, task), probe, CancellationToken.None);

        var uploads = host.Uploads.ToList();
        Assert.That(uploads.Select(upload => (upload.Path, upload.Mode, upload.Owner)), Is.EqualTo(new[]
        {
            ("/srv/api/compose.yaml", "0644", "root:root"),
            ("/srv/api/.env", "0600", "root:root"),
        }));
        var yaml = Encoding.UTF8.GetString(uploads[0].Content);
        Assert.That(yaml, Does.Contain("TOKEN: \"${API_TOKEN}\"").And.Not.Contain(Token));
        Assert.That(Encoding.UTF8.GetString(uploads[1].Content), Does.Contain($"API_TOKEN='{Token}'"));
        var scripts = string.Join("\n---\n", host.Scripts);
        Assert.That(scripts, Does.Contain("docker network inspect 'edge' >/dev/null 2>&1 || docker network create 'edge'")
            .And.Contain("cd '/srv/api'").And.Contain("docker compose -p 'api' up -d --remove-orphans")
            .And.Contain("'http://172.18.0.5:8080/health'"));
    }

    [Test]
    public async Task HealthTimeoutFailsWithRedactedLogs()
    {
        var task = ApiTask();
        var host = new FakeHost(FakeHost.Node())
            .On("sha256sum", "docker\n")
            .On("docker inspect", "api running unhealthy 172.18.0.5\n")
            .On("logs --no-color", $"starting\nusing token {Token}\nfailed\n");
        var probe = await Module.ProbeAsync(Context(host, task), CancellationToken.None);

        var error = Assert.ThrowsAsync<HostException>(() => Module.ApplyAsync(Context(host, task), probe, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("service api did not become healthy within 0s (running, unhealthy, http check failing)")
            .And.Contain("using token (secret)").And.Not.Contain(Token));
    }

    [Test]
    public async Task StoppedContainerFailsImmediately()
    {
        var task = ApiTask(Text("nginx"));
        var host = new FakeHost(FakeHost.Node()).On("sha256sum", "docker\n").On("docker inspect", "api exited none\n");
        var probe = await Module.ProbeAsync(Context(host, task), CancellationToken.None);

        var error = Assert.ThrowsAsync<HostException>(() => Module.ApplyAsync(Context(host, task), probe, CancellationToken.None));

        Assert.That(error!.Message, Does.StartWith("service api stopped (exited)"));
    }

    [Test]
    public void LocalhostHealthUrlsAreRewrittenToTheContainerAddress()
    {
        Assert.That(ComposeHealth.HostUrl("http://localhost:8080/health", "10.1.2.3"), Is.EqualTo("http://10.1.2.3:8080/health"));
        Assert.That(ComposeHealth.HostUrl("http://api.internal/health", "10.1.2.3"), Is.EqualTo("http://api.internal/health"));
    }
}
