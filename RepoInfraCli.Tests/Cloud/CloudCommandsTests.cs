using System.Net;
using System.Text.Json;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Tests.Cloud;

public class CloudCommandsTests
{
    const string Database = """
                            local main = cloud.database_cluster "main" { engine = "pg", version = "17", size = "db-s-1vcpu-1gb", region = "fra1", protect = true }
                            local reports = main.database "reports" { user = "reports" }
                            """;

    CloudHarness harness = null!;

    [SetUp]
    public void SetUp() => harness = new CloudHarness();

    [TearDown]
    public void TearDown() => harness.Dispose();

    [Test]
    public void AdoptTagsAnUnownedDropletAndRenamesAFirewall()
    {
        harness.Script("""
                       local web1 = cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1", tags = { "web" } }
                       cloud.firewall "web" { droplets = { web1.id }, inbound = { { tcp = 22, from = { "10.0.0.0/24" } } } }
                       """);
        var firewall = CloudHarness.Fixture("do-firewall.json");
        firewall["name"] = "web";
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: ["web"]))
            .Account("firewalls", "firewalls", firewall);
        harness.Http
            .On("POST", $"{CloudHarness.DigitalOcean}tags", """{"tag":{"name":"infra:test"}}""", HttpStatusCode.Created)
            .On("POST", $"{CloudHarness.DigitalOcean}tags/infra%3Atest/resources", "", HttpStatusCode.NoContent)
            .On("PUT", $"{CloudHarness.DigitalOcean}firewalls/fb6045f1-cf1d-4ca3-bfac-18832663025b", """{"firewall":{}}""");

        var result = harness.Run("adopt", "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("~ digitalocean droplet web-1: tag droplet 3164444 with infra:test"));
        Assert.That(result.Output, Does.Contain("~ digitalocean firewall web: rename firewall web -> test--web").And.Contain("Adopted 2 resource(s)."));
        var tagged = harness.Http.Sent("POST", "tags/infra%3Atest/resources").Single().Json!;
        Assert.That(tagged["resources"]![0]!["resource_id"]!.GetValue<string>(), Is.EqualTo("3164444"));
        var renamed = harness.Http.Sent("PUT", "firewalls/").Single().Json!;
        Assert.That(renamed["name"]!.GetValue<string>(), Is.EqualTo("test--web"));
        Assert.That(renamed["inbound_rules"]![0]!["ports"]!.GetValue<string>(), Is.EqualTo("22"));
    }

    [Test]
    public void AdoptNeedsConfirmationAndUnknownNamesAreNotFound()
    {
        harness.Script("""cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1" }""");
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: []));

        var unconfirmed = harness.Run("adopt");
        var missing = harness.Run("adopt", "nope", "--yes");

        Assert.That(unconfirmed.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(missing.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(missing.Errors, Does.Contain("No resource named 'nope'").And.Contain("droplet:web-1"));
        Assert.That(harness.Http.Changes, Is.Empty);
    }

    [Test]
    public void DatabaseConnectionStringComesFromLiveStateAndIsSecretInShow()
    {
        harness.Script(Database);
        harness.EmptyAccount().Account("databases", "databases", CloudHarness.Fixture("do-database-cluster.json"));

        var plan = harness.Run("plan", "--resources-only");
        var connection = harness.Cloud.Live.Resolve(new OutputRef(new NodeId("digitalocean.database:main/reports"), "connection_string", true));
        var shown = harness.Run("show", "main/reports");
        var field = harness.Run("show", "main/reports", "--field", "connection_string");

        Assert.That(plan.Output, Does.Contain("no changes"), plan.Output + plan.Errors);
        Assert.That(connection, Is.EqualTo(new StringValue("postgresql://reports:r3p0rtsS3cret@main-do-user-1.db.ondigitalocean.com:25060/reports?sslmode=require")));
        Assert.That(shown.Output, Does.Contain("connection_string").And.Contain("(secret)").And.Not.Contain("r3p0rtsS3cret"));
        Assert.That(field.Output.Trim(), Is.EqualTo("postgresql://reports:r3p0rtsS3cret@main-do-user-1.db.ondigitalocean.com:25060/reports?sslmode=require"));
    }

    [Test]
    public void ShowListsDeclaredAndStrayResourcesAndPrintsOneField()
    {
        harness.Script("""cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1" }""");
        harness.EmptyAccount().Account("droplets", "droplets", CloudHarness.Droplet(tags: ["infra:test"]), CloudHarness.Droplet("old", 2, tags: ["infra:test"]));

        var all = harness.Run("show", "--json");
        var address = harness.Run("show", "web-1", "--field", "ipv4_address");

        Assert.That(all.ExitCode, Is.EqualTo(InfraExitCodes.Success), all.Output + all.Errors);
        var resources = JsonDocument.Parse(all.Output).RootElement.GetProperty("resources");
        Assert.That(resources.EnumerateArray().Select(resource => $"{resource.GetProperty("name").GetString()}={resource.GetProperty("status").GetString()}"),
            Is.EqualTo(new[] { "web-1=owned", "old=not in infra.luau" }));
        Assert.That(address.Output.Trim(), Is.EqualTo("203.0.113.10"));
    }

    [Test]
    public void DestroyDeletesOwnedResourcesInKindOrderAndKeepsProtectedOnes()
    {
        harness.Script("""cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1" }""");
        harness.EmptyAccount()
            .Account("droplets", "droplets", CloudHarness.Droplet(tags: ["infra:test"]), CloudHarness.Droplet("theirs", 9, tags: []))
            .Account("firewalls", "firewalls", CloudHarness.Fixture("do-firewall.json"))
            .Account("databases", "databases", CloudHarness.Fixture("do-database-cluster.json"));
        harness.Http
            .On("DELETE", $"{CloudHarness.DigitalOcean}droplets/3164444", "", HttpStatusCode.NoContent)
            .On("DELETE", $"{CloudHarness.DigitalOcean}firewalls/fb6045f1-cf1d-4ca3-bfac-18832663025b", "", HttpStatusCode.NoContent);

        var unconfirmed = harness.Run("destroy", "--yes");
        var named = harness.Run("destroy", "main", "--yes", "--confirm", "test");
        var result = harness.Run("destroy", "--yes", "--confirm", "test");

        Assert.That(unconfirmed.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(unconfirmed.Errors, Does.Contain("--confirm test"));
        Assert.That(named.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(named.Errors, Does.Contain("database_cluster main is protected; no flag overrides protect or exported"));
        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("digitalocean database_cluster main: kept (protected)").And.Contain("Destroyed 2 resource(s)."));
        Assert.That(harness.Http.Changes.Select(request => $"{request.Method} {request.Url}"), Is.EqualTo(new[]
        {
            $"DELETE {CloudHarness.DigitalOcean}firewalls/fb6045f1-cf1d-4ca3-bfac-18832663025b",
            $"DELETE {CloudHarness.DigitalOcean}droplets/3164444",
        }));
    }

    [Test]
    public void DestroyWithAnEnvOnlyTouchesThatEnvsMarkers()
    {
        harness.Script("""cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1" }""");
        harness.Repo.WriteFile(".repo-infra/envs/prod/.keep", "");
        harness.Repo.WriteFile(".repo-infra/envs/staging/.keep", "");
        var firewall = CloudHarness.Fixture("do-firewall.json");
        firewall["name"] = "test--prod--web";
        var stagingFirewall = CloudHarness.Fixture("do-firewall.json");
        stagingFirewall["id"] = "staging-firewall";
        stagingFirewall["name"] = "test--staging--web";
        harness.EmptyAccount()
            .Account("droplets", "droplets",
                CloudHarness.Droplet(tags: ["infra:test", "infra-env:prod"]),
                CloudHarness.Droplet("staging", 7, tags: ["infra:test", "infra-env:staging"]),
                CloudHarness.Droplet("envless", 8, tags: ["infra:test"]))
            .Account("firewalls", "firewalls", firewall, stagingFirewall);
        harness.Http
            .On("DELETE", $"{CloudHarness.DigitalOcean}droplets/3164444", "", HttpStatusCode.NoContent)
            .On("DELETE", $"{CloudHarness.DigitalOcean}firewalls/fb6045f1-cf1d-4ca3-bfac-18832663025b", "", HttpStatusCode.NoContent);

        var wrongConfirm = harness.Run("destroy", "--env", "prod", "--yes", "--confirm", "test");
        var result = harness.Run("destroy", "--env", "prod", "--yes", "--confirm", "test-prod");

        Assert.That(wrongConfirm.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(wrongConfirm.Errors, Does.Contain("--confirm test-prod"));
        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(harness.Http.Changes.Select(request => $"{request.Method} {request.Url}"), Is.EqualTo(new[]
        {
            $"DELETE {CloudHarness.DigitalOcean}firewalls/fb6045f1-cf1d-4ca3-bfac-18832663025b",
            $"DELETE {CloudHarness.DigitalOcean}droplets/3164444",
        }));
    }

    [Test]
    public void ExportedResourcesCarryTheExportedMarkerButTheirDependenciesDoNot()
    {
        harness.Script("""
                       local key = cloud.ssh_key "deploy" { public_key = infra.file "keys/deploy.pub" }
                       local web1 = cloud.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-2vcpu-4gb", region = "fra1", ssh_keys = { key.fingerprint } }
                       local web = web1:host { user = "root" }
                       return { hosts = { web = web } }
                       """);
        harness.Repo.WriteFile("keys/deploy.pub", CloudPlanTests.DeployKey);
        harness.Account("droplets", "droplets", CloudHarness.Droplet(tags: ["infra:test"]))
            .Account("account/keys", "ssh_keys", CloudHarness.Fixture("do-ssh-key.json"));

        var result = harness.Run("plan", "--resources-only");

        Assert.That(CloudHarness.Lines(result.Output), Does.Contain("  ~ digitalocean droplet web-1: marker infra:test name=web-1 -> infra:test name=web-1 exported"));
        Assert.That(result.Output, Does.Not.Contain("ssh_key deploy"));
    }
}