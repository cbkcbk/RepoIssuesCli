using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Hosts;

namespace RepoInfraCli.Tests.Compose;

[Category("Docker")]
[NonParallelizable]
public class ComposeIntegrationTests
{
    const string Project = "infratest";
    const string Token = "compose-secret-value";

    INetwork network = null!;
    SshdContainer sshd = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        network = new NetworkBuilder().WithName($"infra-test-{Guid.NewGuid():N}").Build();
        await network.CreateAsync();
        sshd = await SshdContainer.StartWithDockerAsync(network);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        await sshd.ExecAsRootAsync("docker", "compose", "-p", Project, "down", "--remove-orphans");
        await sshd.DisposeAsync();
        await network.DisposeAsync();
    }

    [Test]
    public async Task PlanApplyPlanConvergesAndWaitsForHttpHealth()
    {
        using var repo = new TempRepo();
        repo.WriteScript($$"""
                           local compose = require("@infra/compose")
                           local net = compose.network "{{network.Name}}"
                           local box = infra.host "box" { address = "{{sshd.Address}}", user = "nopw", port = {{sshd.Port.ToString(CultureInfo.InvariantCulture)}}, become = true }

                           local function app(h)
                             h:compose "{{Project}}" {
                               services = {
                                 web = {
                                   image = "nginx:alpine",
                                   networks = { net },
                                   environment = { TOKEN = infra.secret "test/token", MODE = "test" },
                                   healthcheck = { http = "http://localhost:80/" },
                                 },
                                 bare = {
                                   image = "python:3.13-slim",
                                   command = { "python", "-m", "http.server", "8000" },
                                   healthcheck = { http = "http://localhost:8000/" },
                                 },
                               },
                             }
                           end

                           infra.play "p" { hosts = { box }, roles = { app } }
                           """);
        var graph = repo.Evaluate().Graph!;
        var planner = new Planner(sshd.Connector(sshd.KnownHostsFile("compose", sshd.HostKeyLine)));
        var secrets = new SecretLookup(new EnvironmentSecretResolver(name => name == EnvironmentSecretResolver.VariableName("test/token") ? Token : null), repo.Workspace, null, new SecretRedactor());
        var options = new PlanOptions { Workspace = repo.Workspace, Log = InfraLog.Silent(), Secrets = secrets };

        InfraPlan first;
        ApplyResult applied;
        await using (var run = await planner.PlanAsync(graph, options))
        {
            first = run.Plan;
            applied = await new Applier().ApplyAsync(run, InfraLog.Silent());
        }

        await using var second = await planner.PlanAsync(graph, options);

        Assert.That(first.Problems, Is.Empty);
        Assert.That(first.Tasks.Single().Changes.First().Text, Does.StartWith($"create /opt/{Project}/compose.yaml"));
        Assert.That(applied.Succeeded, Is.True, applied.Failure?.Error);
        Assert.That(second.Plan.Problems, Is.Empty);
        Assert.That(second.Plan.Tasks.SelectMany(task => task.Changes).Select(change => change.Text), Is.Empty);
        Assert.That(await sshd.ExecAsRootAsync("stat", "-c", "%a %U", $"/opt/{Project}/.env"), Is.EqualTo("600 root\n"));
        Assert.That(await sshd.ExecAsRootAsync("cat", $"/opt/{Project}/compose.yaml"), Does.Not.Contain(Token));
        Assert.That(await sshd.ExecAsRootAsync("docker", "compose", "-p", Project, "exec", "-T", "web", "printenv", "TOKEN"), Is.EqualTo(Token + "\n"));
        Assert.That(await sshd.ExecAsRootAsync("docker", "compose", "-p", Project, "exec", "-T", "bare", "sh", "-c", "command -v curl wget || echo none"), Is.EqualTo("none\n"));
        foreach (var service in (string[])["web", "bare"])
        {
            var id = (await sshd.ExecAsRootAsync("docker", "compose", "-p", Project, "ps", "-q", service)).Trim();
            Assert.That(await sshd.ExecAsRootAsync("docker", "inspect", "-f", "{{.State.Health.Status}}", id), Is.EqualTo("healthy\n"), service);
        }
    }
}