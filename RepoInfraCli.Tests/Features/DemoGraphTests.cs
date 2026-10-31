using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Postgres;

namespace RepoInfraCli.Tests.Features;

public class DemoGraphTests
{
    TempRepo repo = null!;
    InfraGraph graph = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.CopyDemo();
        var result = repo.Evaluate();
        Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Is.EqualTo(new[] { DiagnosticCodes.RepoUnavailable }), string.Join("\n", result.Diagnostics));
        graph = result.Graph!;
    }

    [OneTimeTearDown]
    public void TearDown() => repo.Dispose();

    ResourceNode Resource(string kind, string name) => graph.Resources.Single(resource => resource.Kind == kind && resource.Name == name);

    [Test]
    public void ResourceCountsByKind()
    {
        var counts = graph.Resources.GroupBy(resource => resource.Kind).ToDictionary(group => group.Key, group => group.Count());

        Assert.That(counts, Is.EquivalentTo(new Dictionary<string, int>
        {
            ["zone"] = 1, ["ssh_key"] = 1, ["droplet"] = 1, ["firewall"] = 1, ["record"] = 2, ["spaces_bucket"] = 1, ["database_cluster"] = 2, ["database"] = 2,
        }));
        Assert.That(graph.Resources.Where(resource => resource.Existing).Select(resource => resource.Id.Value),
            Is.EquivalentTo(new[] { "cloudflare.zone:snappy.no", "digitalocean.database_cluster:legacy-pg", "digitalocean.database:legacy-pg/crm" }));
    }

    [Test]
    public void ProjectProvidersAndSecrets()
    {
        Assert.That(graph.Project, Is.EqualTo(new ProjectNode("snappy", true, new SourceLocation(".repo-infra/infra.luau", 23))));
        Assert.That(graph.Providers.Select(provider => provider.Id.Value), Is.EqualTo(new[] { "digitalocean", "cloudflare", "repo" }));
        Assert.That(graph.Providers[0].Options["token"], Is.EqualTo(new SecretRef("digitalocean/token")));
        Assert.That(graph.Providers[0].Options[DigitalOceanModule.SpacesAccessKey], Is.EqualTo(new SecretRef("digitalocean/spaces_access_key")));
        Assert.That(graph.Secrets.Select(secret => secret.Path), Is.EqualTo(new[]
        {
            "digitalocean/token", "digitalocean/spaces_access_key", "digitalocean/spaces_secret_key", "cloudflare/token", "postgres/api", "cloudflare/dns_token",
            "spaces/backups",
        }));
    }

    [Test]
    public void OutputsBecomeDependencies()
    {
        var droplet = Resource("droplet", "web-1");
        var firewall = Resource("firewall", "web");
        var record = Resource("record", "api");

        Assert.That(droplet.DependsOn, Is.EqualTo(new[] { new NodeId("digitalocean.ssh_key:deploy") }));
        Assert.That(firewall.DependsOn, Is.EqualTo(new[] { droplet.Id }));
        Assert.That(record.Options["content"], Is.EqualTo(new OutputRef(droplet.Id, "ipv4_address")));
        Assert.That(record.DependsOn, Is.EquivalentTo(new[] { droplet.Id, new NodeId("cloudflare.zone:snappy.no") }));
        Assert.That(record.Id, Is.EqualTo(new NodeId("cloudflare.record:api.snappy.no/A")));
        Assert.That(Resource("ssh_key", "deploy").Options["public_key"], Is.InstanceOf<FileRef>());
    }

    [Test]
    public void ProtectAndExportFlags()
    {
        Assert.That(Resource("record", "@").Protect, Is.True);
        Assert.That(Resource("record", "@").Options.Entries.ContainsKey("protect"), Is.False);
        Assert.That(Resource("spaces_bucket", "snappy-backups").Protect, Is.True);
        Assert.That(graph.Resources.Where(resource => resource.Exported).Select(resource => resource.Id.Value),
            Is.EquivalentTo(new[] { "digitalocean.droplet:web-1", "digitalocean.database_cluster:main", "digitalocean.database:main/reports" }));
        Assert.That(graph.Exports!.Roles, Is.EqualTo(new[] { "baseline", "docker" }));
        Assert.That(graph.Exports.Networks["edge"], Is.EqualTo(new NodeId("compose.network:edge")));
    }

    [Test]
    public void HostsIncludeDropletHostAndExternalHost()
    {
        var web = graph.Hosts.Single(host => host.Name == "web-1");
        var nas = graph.Hosts.Single(host => host.Name == "nas");

        Assert.That(web.Address, Is.EqualTo(new OutputRef(new NodeId("digitalocean.droplet:web-1"), "ipv4_address")));
        Assert.That(web.Resource, Is.EqualTo(new NodeId("digitalocean.droplet:web-1")));
        Assert.That(web.User, Is.EqualTo("root"));
        Assert.That(nas.External, Is.True);
        Assert.That(nas.Provider, Is.EqualTo(new NodeId("repo")));
    }

    [Test]
    public void PlaysExpandRolesAppsAndExternalRoles()
    {
        Assert.That(graph.Plays.Select(play => play.Name), Is.EqualTo(new[] { "edge", "data", "apps", "build", "nas" }));
        Assert.That(graph.Play("edge")!.Roles, Is.EqualTo(new[] { "baseline", "docker", "edge" }));
        Assert.That(graph.Play("build")!.Runs.Single(run => run.External).Role, Is.EqualTo("platform-infra/node_exporter"));

        var deploy = graph.Play("apps")!.Runs.Single();
        var compose = deploy.Tasks.Single();
        var service = (MapValue)((MapValue)compose.Options["services"]!)["api"]!;
        Assert.That(deploy.Role, Is.EqualTo("app:api"));
        Assert.That(service["image"], Is.EqualTo(new OutputRef(new NodeId("dotnet.container:Api"), "image")));
        var environment = (MapValue)service["environment"]!;
        Assert.That(environment["ConnectionStrings__Db"], Is.EqualTo(new OutputRef(new NodeId("postgres.database:api"), "connection_string", true)));
        Assert.That(environment["ConnectionStrings__Db"]!.Display(), Is.EqualTo("(secret)"));
        var labels = (MapValue)service["labels"]!;
        Assert.That(labels.GetString("traefik.http.routers.api-snappy-no.rule"), Is.EqualTo("Host(`api.snappy.no`)"));
        Assert.That(labels.GetString("traefik.http.routers.api-snappy-no.tls.certresolver"), Is.EqualTo("cf"));
    }

    [Test]
    public void TraefikArgsAndPostgresServicesRender()
    {
        var edge = graph.Play("edge")!.Runs.Single(run => run.Role == "edge").Tasks.Single(task => task.Kind == "compose");
        var traefik = (MapValue)((MapValue)edge.Options["services"]!)["traefik"]!;
        var command = ((ListValue)traefik["command"]!).Items.Select(item => item.Display()).ToList();

        Assert.That(command, Does.Contain("--entrypoints.websecure.address=:443")
            .And.Contain("--certificatesresolvers.cf.acme.dnschallenge.provider=cloudflare")
            .And.Contain("--providers.docker.exposedbydefault=false")
            .And.Contain("--providers.docker.network=edge"));

        var tasks = graph.Play("data")!.Runs.Single().Tasks;
        Assert.That(tasks.Select(task => $"{task.Kind} {task.Name}"), Is.EqualTo(new[] { $"file {BackupScript.HostPath}", "compose postgres" }));
        var services = (MapValue)tasks[1].Options["services"]!;
        var postgres = (MapValue)services["postgres"]!;
        Assert.That(postgres.GetString("image"), Is.EqualTo("postgres:17"));
        Assert.That(((MapValue)postgres["environment"]!)["POSTGRES_PASSWORD"], Is.EqualTo(new SecretRef("postgres/api")));
        Assert.That(((ListValue)postgres["networks"]!).Items.Single(), Is.EqualTo(new NodeRef(new NodeId("compose.network:db"))));
        Assert.That(services.Entries.Keys, Is.EquivalentTo(new[] { "postgres", "backup" }));
    }

    [Test]
    public void BaselineHandlerIsLinkedFromTheFileTask()
    {
        var baseline = graph.Play("edge")!.Runs.Single(run => run.Role == "baseline").Tasks;
        var handler = baseline.Single(task => task.Kind == "handler");
        var hardening = baseline.Single(task => task.Kind == "file");

        Assert.That(hardening.Notifies, Is.EqualTo(handler.Id));
        Assert.That(hardening.Name, Is.EqualTo("/etc/ssh/sshd_config.d/10-hardening.conf"));
        Assert.That(handler.Options.GetString("state"), Is.EqualTo("restarted"));
    }

    [Test]
    public void MarkersFollowTheOwnershipConvention()
    {
        Assert.That(graph.Marker.For(Resource("droplet", "web-1")), Is.EqualTo("infra:snappy name=web-1 exported"));
        Assert.That(graph.Marker.For(Resource("record", "@")), Is.EqualTo("infra:snappy name=@ protect"));
        Assert.That(graph.Marker.PrefixedName("web"), Is.EqualTo("snappy--web"));
        Assert.That(new Marker("snappy", "prod").For("db", protect: true, exported: true), Is.EqualTo("infra:snappy env=prod name=db protect exported"));
        Assert.That(new Marker("snappy", "prod").PrefixedName("web"), Is.EqualTo("snappy--prod--web"));
        Assert.That(new Marker("snappy", "prod").Owner, Is.EqualTo("snappy-prod"));
        Assert.That(Resource("firewall", "web").UsesNamePrefix, Is.True);
        Assert.That(Resource("droplet", "web-1").UsesNamePrefix, Is.False);
    }

    [Test]
    public void ReferenceModeKeepsResourcesAndExportsButNoRuns()
    {
        var reference = repo.Evaluate(mode: EvaluationMode.Reference);

        Assert.That(reference.Errors, Is.Empty);
        Assert.That(reference.Graph!.Resources, Has.Count.EqualTo(graph.Resources.Count));
        Assert.That(reference.Graph.Tasks, Is.Empty);
        Assert.That(reference.Graph.Exports!.Hosts.Keys, Is.EqualTo(new[] { "web" }));
    }
}