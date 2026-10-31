using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Cloud;

[Category("Live")]
[Explicit("Talks to real DigitalOcean and Cloudflare accounts; set INFRA_LIVE_DIGITALOCEAN_TOKEN (and optionally INFRA_LIVE_CLOUDFLARE_TOKEN and INFRA_LIVE_CLOUDFLARE_ZONE).")]
public class LiveCloudTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InconclusiveException($"{name} is not set");

    RunResult Run(Dictionary<string, string> variables, params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var secrets = new SecretEnvironment(name => variables.GetValueOrDefault(name), Path.Combine(repo.Root, "no-key"));
        var exitCode = InfraApp.Create(output, errors, secrets: secrets).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    [Test]
    public void PlanningAnUnusedProjectAgainstTheRealAccountOnlyReads()
    {
        var variables = new Dictionary<string, string> { ["INFRA_SECRET_DIGITALOCEAN_TOKEN"] = Require("INFRA_LIVE_DIGITALOCEAN_TOKEN") };
        var project = $"live-{Guid.NewGuid():N}"[..20];
        repo.WriteInfra($$"""
            local infra = require("@infra")
            local digitalocean = require("@infra/digitalocean")
            infra.project "{{project}}" { prune = true }
            local cloud = digitalocean.provider { token = infra.secret "digitalocean/token" }
            cloud.droplet "probe" { image = "ubuntu-24-04-x64", size = "s-1vcpu-512mb-10gb", region = "fra1" }
            cloud.vpc "probe" { region = "fra1" }
            """);

        var result = Run(variables, "plan", "--resources-only", "-v");

        TestContext.Out.WriteLine(result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("+ digitalocean droplet probe: create").And.Contain("+ digitalocean vpc probe: create"));
        Assert.That(result.Output, Does.Not.Contain("- digitalocean"));
    }

    [Test]
    public void CloudflareZoneLookupAndRecordPlanAgainstTheRealAccount()
    {
        var variables = new Dictionary<string, string> { ["INFRA_SECRET_CLOUDFLARE_TOKEN"] = Require("INFRA_LIVE_CLOUDFLARE_TOKEN") };
        var zone = Require("INFRA_LIVE_CLOUDFLARE_ZONE");
        repo.WriteInfra($$"""
            local infra = require("@infra")
            local cloudflare = require("@infra/cloudflare")
            infra.project "live-probe" {}
            local dns = cloudflare.provider { api_token = infra.secret "cloudflare/token" }
            local zone = dns.zone "{{zone}}"
            zone.record "infra-live-probe" { type = "TXT", content = "probe" }
            """);

        var result = Run(variables, "plan", "--resources-only");

        TestContext.Out.WriteLine(result.Output + result.Errors);
        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success));
        Assert.That(result.Output, Does.Contain($"cloudflare record infra-live-probe.{zone} TXT"));
    }
}
