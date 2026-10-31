using System.Text.Json.Nodes;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Tests.Hosts;

namespace RepoInfraCli.Tests.Cloud;

public class CloudPlanTests
{
    const string WebDroplet = """
                              cloud.droplet "web-1" {
                                  image = "ubuntu-24-04-x64",
                                  size = "s-2vcpu-4gb",
                                  region = "fra1",
                                  tags = { "web" },
                              }
                              """;

    public const string DeployKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHy8kCtkaMsfaJx4rW3OUKD4E7dOQGsGcGv3FIC1k6Iv deploy@example";

    CloudHarness harness = null!;

    [SetUp]
    public void SetUp() => harness = new CloudHarness();

    [TearDown]
    public void TearDown() => harness.Dispose();

    [Test]
    public void DemoAgainstAnEmptyAccountCreatesEveryResourceAndLooksUpExistingOnes()
    {
        harness.Repo.CopyDemo();
        var demo = File.ReadAllText(harness.Repo.Workspace.EntryFile).Replace("file \"~/.ssh/deploy.pub\"", "file \"keys/deploy.pub\"");
        harness.Repo.WriteInfra(demo);
        harness.Repo.WriteFile("keys/deploy.pub", DeployKey + "\n");
        harness.EmptyAccount()
            .Account("databases", "databases", CloudHarness.Fixture("do-legacy-cluster.json"));
        harness.Http.On("GET", "https://api.cloudflare.com/client/v4/zones/0b1d7e2f3c4a5b6d7e8f9a0b1c2d3e4f/dns_records?per_page=100&page=1",
            FakeCloud.Fixture("cf-records.json").Replace("example.com", "snappy.no"));

        var result = harness.Run("plan", "--resources-only");

        TestContext.Out.WriteLine(result.Output);
        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        var lines = CloudHarness.Lines(result.Output);
        Assert.That(lines, Does.Contain("  + digitalocean ssh_key deploy: create (public_key ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHy8kCtkaMsfaJx4rW3OUKD4E7dOQGsGcGv3FIC1k6Iv)"));
        Assert.That(lines, Does.Contain("  + digitalocean droplet web-1: create (region fra1, image ubuntu-24-04-x64, size s-2vcpu-4gb, ssh_keys (known after apply), tags [web])"));
        Assert.That(lines, Has.Some.StartsWith("  + digitalocean firewall web: create (inbound [tcp 22 [10.0.0.0/24, 84.212.0.0/16], tcp 443 [").And.Some.Contains("droplets (known after apply)"));
        Assert.That(lines, Does.Contain("  + cloudflare record api.snappy.no A: create (type A, content (known after apply), proxied true, ttl 1)"));
        Assert.That(lines, Does.Contain("  + cloudflare record snappy.no A: create (type A, content (known after apply), proxied true, ttl 1)"));
        Assert.That(lines, Does.Contain("  + digitalocean spaces_bucket snappy-backups: create (region fra1, acl private, lifecycle [postgres/ 14d])"));
        Assert.That(lines, Does.Contain("  + digitalocean database_cluster main: create (engine pg, region fra1, version 17, size db-s-1vcpu-1gb, nodes 1, trusted_sources (known after apply))"));
        Assert.That(lines, Does.Contain("  + digitalocean database main/reports: create (user reports)"));
        Assert.That(result.Output, Does.Not.Contain("legacy-pg").And.Not.Contain("error"));
        Assert.That(result.Output, Does.Contain("Plan: 8 to add, 0 to change, 0 to remove, 0 to run."));
        Assert.That(harness.Http.Changes, Is.Empty);
        Assert.That(harness.Connector.Connects, Is.Empty);
    }

    [Test]
    public void MissingTokenIsAPlanProblemWithTheSecretsOwnMessage()
    {
        harness.Variables.Remove("INFRA_SECRET_DIGITALOCEAN_TOKEN");
        harness.Script(WebDroplet);

        var result = harness.Run("plan", "--resources-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("error digitalocean.provider:").And.Contain("digitalocean/token"));
        Assert.That(harness.Http.Requests, Is.Empty);
    }

    [Test]
    public void OwnedDropletInSyncHasNoChangesAndExitCodeIsZero()
    {
        harness.Script(WebDroplet);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: ["web", "infra:test"]));

        var result = harness.Run("plan", "--resources-only", "--exit-code");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(CloudHarness.Lines(result.Output), Does.Contain("  no changes"));
    }

    [Test]
    public void TagDriftIsAnUpdateAndExitCodeReportsDrift()
    {
        harness.Script(WebDroplet);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: ["infra:test", "old"]));

        var result = harness.Run("plan", "--resources-only", "--exit-code");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Drift), result.Output + result.Errors);
        Assert.That(CloudHarness.Lines(result.Output), Does.Contain("  ~ digitalocean droplet web-1: tags [old] -> [web]"));
    }

    [Test]
    public void CreateOnlyFieldsWarnAndReplaceFieldsShowReplace()
    {
        harness.Script(WebDroplet.Replace("tags = { \"web\" },", "tags = { \"web\" },\n    monitoring = false,"));
        harness.Account("droplets", "droplets", CloudHarness.Droplet(region: "ams3", tags: ["web", "infra:test"]));

        var result = harness.Run("plan", "--resources-only");
        var lines = CloudHarness.Lines(result.Output);

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(lines, Does.Contain("  -/+ digitalocean droplet web-1: replace: region ams3 -> fra1"));
        Assert.That(lines, Does.Contain("  ? digitalocean droplet web-1: monitoring true -> false is create-only; not changed (recreate the droplet to change it)"));
        Assert.That(result.Output, Does.Contain("1 to replace"));
    }

    [Test]
    public void SizeChangeResizesTheDropletInPlaceWithPowerOffAndOn()
    {
        harness.Script(WebDroplet);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(size: "s-1vcpu-1gb", tags: ["web", "infra:test"]));
        const string actions = $"{CloudHarness.DigitalOcean}droplets/3164444/actions";
        static string Action(int id, string status) => $$$"""{"action":{"id":{{{id}}},"status":"{{{status}}}"}}""";
        harness.Http
            .On("POST", actions, Action(1, "in-progress"), System.Net.HttpStatusCode.Created)
            .On("POST", actions, Action(2, "in-progress"), System.Net.HttpStatusCode.Created)
            .On("POST", actions, Action(3, "in-progress"), System.Net.HttpStatusCode.Created)
            .On("GET", $"{actions}/1", Action(1, "in-progress"))
            .On("GET", $"{actions}/1", Action(1, "completed"))
            .On("GET", $"{actions}/2", Action(2, "completed"))
            .On("GET", $"{actions}/3", Action(3, "completed"))
            .On("GET", $"{CloudHarness.DigitalOcean}droplets/3164444", new JsonObject { ["droplet"] = CloudHarness.Droplet(tags: ["web", "infra:test"]) }.ToJsonString());

        var plan = harness.Run("plan", "--resources-only");
        var apply = harness.Run("apply", "--resources-only", "--yes");

        Assert.That(CloudHarness.Lines(plan.Output), Does.Contain("  ~ digitalocean droplet web-1: size s-1vcpu-1gb -> s-2vcpu-4gb (power-off resize)"));
        Assert.That(plan.Output, Does.Not.Contain("replace"));
        Assert.That(apply.ExitCode, Is.EqualTo(InfraExitCodes.Success), apply.Output + apply.Errors);
        var posted = harness.Http.Changes.Select(request => request.Json!.ToJsonString()).ToList();
        Assert.That(posted, Is.EqualTo(new[]
        {
            """{"type":"power_off"}""",
            """{"type":"resize","size":"s-2vcpu-4gb","disk":false}""",
            """{"type":"power_on"}""",
        }));
        Assert.That(harness.Http.Sent("GET", "actions/1"), Has.Count.EqualTo(2));
    }

    [Test]
    public void FailedResizeActionFailsTheApply()
    {
        harness.Script(WebDroplet);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(size: "s-1vcpu-1gb", tags: ["web", "infra:test"]));
        const string actions = $"{CloudHarness.DigitalOcean}droplets/3164444/actions";
        harness.Http
            .On("POST", actions, """{"action":{"id":1,"status":"in-progress"}}""", System.Net.HttpStatusCode.Created)
            .On("GET", $"{actions}/1", """{"action":{"id":1,"status":"errored"}}""");

        var apply = harness.Run("apply", "--resources-only", "--yes");

        Assert.That(apply.ExitCode, Is.Not.EqualTo(InfraExitCodes.Success));
        Assert.That(apply.Output + apply.Errors, Does.Contain("droplet web-1 power_off failed"));
        Assert.That(harness.Http.Changes, Has.Count.EqualTo(1));
    }

    [Test]
    public void ProtectBlocksReplaceAndMarkerFlagsAreUpdated()
    {
        harness.Script(WebDroplet.Replace("tags = { \"web\" },", "tags = { \"web\" },\n    protect = true,"));
        harness.Account("droplets", "droplets", CloudHarness.Droplet(region: "ams3", tags: ["web", "infra:test"]));

        var result = harness.Run("plan", "--resources-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("replace: region ams3 -> fra1; marker infra:test name=web-1 -> infra:test name=web-1 protect"));
        Assert.That(result.Output, Does.Contain("error digitalocean droplet \"web-1\" is protected; replacing it would destroy it (region ams3 -> fra1)"));
    }

    [Test]
    public void UnownedResourceWithADeclaredNameFailsThePlanUntilAdopted()
    {
        harness.Script(WebDroplet);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: ["web"]));

        var result = harness.Run("plan", "--resources-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("already exists (web-1, id 3164444) without an infra marker; run 'infra adopt web-1' to take it over"));
    }

    [Test]
    public void PruneRemovesOwnedStraysButNeverProtectedOrExportedAndApplyNeedsAllowDestroy()
    {
        harness.Script(WebDroplet, prune: true);
        harness.EmptyAccount().Account("droplets", "droplets",
            CloudHarness.Droplet(tags: ["web", "infra:test"]),
            CloudHarness.Droplet("old", 2, tags: ["infra:test"]),
            CloudHarness.Droplet("kept", 3, tags: ["infra:test", "infra-protect"]),
            CloudHarness.Droplet("shared", 4, tags: ["infra:test", "infra-exported"]),
            CloudHarness.Droplet("other", 5, tags: ["infra:someone-else"]));
        harness.Http.On("DELETE", $"{CloudHarness.DigitalOcean}droplets/2", "", System.Net.HttpStatusCode.NoContent);

        var plan = harness.Run("plan", "--resources-only");
        var refused = harness.Run("apply", "--resources-only", "--yes");
        var applied = harness.Run("apply", "--resources-only", "--yes", "--allow-destroy");

        Assert.That(CloudHarness.Lines(plan.Output), Does.Contain("  - digitalocean droplet old: delete 2 (no longer in infra.luau)"));
        Assert.That(plan.Output, Does.Not.Contain("kept").And.Not.Contain("shared").And.Not.Contain("other"));
        Assert.That(refused.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(refused.Errors, Does.Contain("--allow-destroy"));
        Assert.That(applied.ExitCode, Is.EqualTo(InfraExitCodes.Success), applied.Output + applied.Errors);
        Assert.That(harness.Http.Changes.Select(request => $"{request.Method} {request.Url}"), Is.EqualTo(new[] { $"DELETE {CloudHarness.DigitalOcean}droplets/2" }));
    }

    [Test]
    public void ApplyCreatesTheDropletThenPlansItsHostTrustingTheNewKey()
    {
        harness.Script("""
                       local web1 = cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1" }
                       local web = web1:host { user = "root" }
                       infra.play "base" { hosts = { web }, roles = { function(h) h:apt { packages = { "curl" } } end } }
                       """);
        var created = CloudHarness.Droplet(tags: ["infra:test"]);
        var booting = created.DeepClone().AsObject();
        booting["status"] = "new";
        booting["networks"] = new JsonObject { ["v4"] = new JsonArray(), ["v6"] = new JsonArray() };
        harness.Account("droplets", "droplets");
        harness.Http
            .On("POST", $"{CloudHarness.DigitalOcean}droplets", new JsonObject { ["droplet"] = booting.DeepClone() }.ToJsonString(), System.Net.HttpStatusCode.Accepted)
            .On("GET", $"{CloudHarness.DigitalOcean}droplets/3164444", new JsonObject { ["droplet"] = booting }.ToJsonString())
            .On("GET", $"{CloudHarness.DigitalOcean}droplets/3164444", new JsonObject { ["droplet"] = created }.ToJsonString());
        harness.Connector = new FakeConnector().Host("web-1", host => host.On("dpkg-query", "pkg curl un \n"));

        var plan = harness.Run("plan");
        var result = harness.Run("apply", "--yes");

        Assert.That(plan.Output, Does.Contain("web-1 (address known after apply; skipped)"));
        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        var body = harness.Http.Sent("POST", "droplets").Single().Json!;
        Assert.That(body["name"]!.GetValue<string>(), Is.EqualTo("web-1"));
        Assert.That(body["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()), Is.EqualTo(new[] { "infra:test" }));
        var connect = harness.Connector.Connects.Single();
        Assert.That(connect.Options.Address, Is.EqualTo("203.0.113.10"));
        Assert.That(connect.Options.TrustOnFirstUse, Is.True);
        Assert.That(harness.Connector.Opened["web-1"].Scripts, Has.Some.Contains("install 'curl'"));
        Assert.That(result.Output, Does.Contain("done digitalocean droplet > create web-1").And.Contain("done web-1 role1 > apt"));
    }

    [Test]
    public void RecordContentDriftIsPatchedWithTheMarkerComment()
    {
        harness.Script("""
                       local zone = dns.zone "example.com"
                       zone.record "api" { type = "A", content = "198.51.100.7", proxied = true }
                       """);
        harness.Http
            .On("GET", CloudHarness.Zones, FakeCloud.Fixture("cf-zones.json"))
            .On("GET", CloudHarness.ExampleRecords, FakeCloud.Fixture("cf-records.json"))
            .On("PATCH", $"https://api.cloudflare.com/client/v4/zones/{CloudHarness.ExampleZone}/dns_records/372e67954025e0ba6aaa6d586b9e0b59",
                """{"success":true,"errors":[],"messages":[],"result":{"id":"372e67954025e0ba6aaa6d586b9e0b59","name":"api.example.com","type":"A","content":"198.51.100.7","proxied":true,"ttl":1,"comment":"infra:test name=api"}}""");

        var plan = harness.Run("plan", "--resources-only");
        var apply = harness.Run("apply", "--resources-only", "--yes");

        Assert.That(CloudHarness.Lines(plan.Output), Does.Contain("  ~ cloudflare record api.example.com A: content 198.51.100.4 -> 198.51.100.7"));
        Assert.That(apply.ExitCode, Is.EqualTo(InfraExitCodes.Success), apply.Output + apply.Errors);
        var patch = harness.Http.Sent("PATCH", "dns_records").Single().Json!;
        Assert.That(patch["content"]!.GetValue<string>(), Is.EqualTo("198.51.100.7"));
        Assert.That(patch["comment"]!.GetValue<string>(), Is.EqualTo("infra:test name=api"));
        Assert.That(patch["name"]!.GetValue<string>(), Is.EqualTo("api.example.com"));
    }

    [Test]
    public void ResourcesOnlyAndHostsOnlyCannotBeCombined()
    {
        harness.Script(WebDroplet);

        var result = harness.Run("plan", "--resources-only", "--hosts-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("can't be combined"));
    }

    [Test]
    public void FirewallIsOwnedByItsNamePrefixAndDiffsRules()
    {
        harness.Script("""
                       local web1 = cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1" }
                       cloud.firewall "web" { droplets = { web1.id }, inbound = { { tcp = 22, from = { "10.0.0.0/24" } }, { tcp = 443 } } }
                       """);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: ["infra:test"]))
            .Account("firewalls", "firewalls", CloudHarness.Fixture("do-firewall.json"));

        var result = harness.Run("plan", "--resources-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(CloudHarness.Lines(result.Output),
            Does.Contain("  ~ digitalocean firewall web: inbound [tcp 22 [10.0.0.0/24]] -> [tcp 22 [10.0.0.0/24], tcp 443 [0.0.0.0/0, ::/0]]"));
    }

    [Test]
    public void SshKeyFileMissingIsAPlanProblem()
    {
        harness.Script("""cloud.ssh_key "deploy" { public_key = infra.file "keys/missing.pub" }""");
        harness.Account("account/keys", "ssh_keys");

        var result = harness.Run("plan", "--resources-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("digitalocean ssh_key \"deploy\": file keys/missing.pub does not exist"));
    }

    [Test]
    public void DemoStillValidates()
    {
        harness.Repo.CopyDemo();

        Assert.That(harness.Run("validate").ExitCode, Is.EqualTo(InfraExitCodes.Success));
        Assert.That(File.Exists(Path.Combine(harness.Repo.Folder, InfraWorkspace.EntryFileName)), Is.True);
    }
}