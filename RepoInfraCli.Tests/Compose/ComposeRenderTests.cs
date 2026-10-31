using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;
using static RepoInfraCli.Tests.Hosts.Options;

namespace RepoInfraCli.Tests.Compose;

public class ComposeRenderTests
{
    const string Image = "registry.digitalocean.com/snappy/api:0123abc";

    static readonly StaticLiveValues DemoLive = new(
        ("dotnet.container:Api", "image", Image),
        ("postgres.database:api", "connection_string", "Host=postgres;Database=api;Password=pa$$'word"),
        ("digitalocean.database:main/reports", "connection_string", "Host=reports;Password=r"),
        ("digitalocean.database:legacy-pg/crm", "connection_string", "Host=crm;Password=c"));

    static InfraGraph graph = null!;

    [OneTimeSetUp]
    public void Evaluate()
    {
        using var repo = new TempRepo();
        repo.CopyDemo();
        graph = repo.Evaluate().Graph!;
    }

    static TaskNode DemoTask(string play, string name) => graph.Play(play)!.Runs.SelectMany(run => run.Tasks).Single(task => task.Kind == TaskKinds.Compose && task.Name == name);

    static string Secret(SecretRef secret) => $"value-of-{secret.Path}";

    [Test]
    public void DemoApiRendersSecretsAsVariablesAndNetworksAsExternal()
    {
        var task = DemoTask("apps", "api");

        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, DemoLive, strict: true);

        Assert.That(files.Yaml, Is.EqualTo("""
            name: "api"
            services:
              api:
                image: "registry.digitalocean.com/snappy/api:0123abc"
                restart: "unless-stopped"
                networks:
                  - "edge"
                  - "db"
                environment:
                  ASPNETCORE_URLS: "http://+:8080"
                  ConnectionStrings__Crm: "${API_CONNECTIONSTRINGS__CRM}"
                  ConnectionStrings__Db: "${API_CONNECTIONSTRINGS__DB}"
                  ConnectionStrings__Reports: "${API_CONNECTIONSTRINGS__REPORTS}"
                labels:
                  traefik.enable: "true"
                  traefik.http.routers.api-snappy-no.entrypoints: "websecure"
                  traefik.http.routers.api-snappy-no.rule: "Host(`api.snappy.no`)"
                  traefik.http.routers.api-snappy-no.tls: "true"
                  traefik.http.routers.api-snappy-no.tls.certresolver: "cf"
                  traefik.http.services.api-snappy-no.loadbalancer.server.port: "8080"
                x-infra-healthcheck-http: "http://localhost:8080/health"
            networks:
              db:
                external: true
              edge:
                external: true

            """.ReplaceLineEndings("\n")));
        Assert.That(files.Env, Is.EqualTo(ComposeEnvFile.Header + """
            API_CONNECTIONSTRINGS__CRM='Host=crm;Password=c'
            API_CONNECTIONSTRINGS__DB="Host=postgres;Database=api;Password=pa\$\$'word"
            API_CONNECTIONSTRINGS__REPORTS='Host=reports;Password=r'

            """.ReplaceLineEndings("\n")));
        Assert.That(files.Directory, Is.EqualTo("/opt/api"));
        Assert.That(files.Networks, Is.EqualTo(new[] { "db", "edge" }));
        Assert.That(files.Services.Single(), Is.EqualTo(new ComposeService("api", Image, "http://localhost:8080/health")));
        Assert.That(files.Unknown, Is.Empty);
        Assert.That(files.SecretValues, Has.Count.EqualTo(3));
    }

    [Test]
    public void DemoTraefikRendersArgsAndTheDnsTokenFromTheEnvFile()
    {
        var task = DemoTask("edge", "traefik");

        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, NoLiveValues.Instance, strict: true);

        Assert.That(files.Yaml, Does.StartWith("""
            name: "traefik"
            services:
              traefik:
                image: "traefik:v3.5"
                restart: "unless-stopped"
                command:
                  - "--entrypoints.websecure.address=:443"

            """.ReplaceLineEndings("\n")));
        Assert.That(files.Yaml, Does.Contain("      - \"--certificatesresolvers.cf.acme.dnschallenge.provider=cloudflare\"\n")
            .And.Contain("      - \"--providers.docker.network=edge\"\n")
            .And.Contain("""
                    ports:
                      - "443:443"
                    networks:
                      - "edge"
                    volumes:
                      - "/var/run/docker.sock:/var/run/docker.sock:ro"
                      - "./acme:/acme"
                    environment:
                      CF_DNS_API_TOKEN: "${TRAEFIK_CF_DNS_API_TOKEN}"
                networks:
                  edge:
                    external: true

                """.ReplaceLineEndings("\n")));
        Assert.That(files.Yaml, Does.Not.Contain("value-of-"));
        Assert.That(files.Env, Does.EndWith("TRAEFIK_CF_DNS_API_TOKEN='value-of-cloudflare/dns_token'\n"));
        Assert.That(files.Directory, Is.EqualTo("/opt/traefik"));
    }

    [Test]
    public void DemoPostgresServicesRenderGenerically()
    {
        var task = DemoTask("data", "postgres");

        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, NoLiveValues.Instance, strict: false);

        Assert.That(files.Services.Select(service => service.Name), Is.EqualTo(new[] { "backup", "backup_reports", "postgres" }));
        Assert.That(files.Yaml, Does.Contain("      POSTGRES_PASSWORD: \"${POSTGRES_POSTGRES_PASSWORD}\"\n")
            .And.Contain("      test:\n        - \"CMD-SHELL\"\n        - \"pg_isready -U api -d api\"\n"));
        Assert.That(files.Env, Does.Contain("POSTGRES_POSTGRES_PASSWORD='value-of-postgres/api'").And.Contain("BACKUP_BACKUP_CREDENTIALS='value-of-spaces/backups'"));
        Assert.That(files.Unknown, Does.Contain("backup.environment.DATABASE_URL").And.Contain("backup_reports.environment.BACKUP_ENDPOINT"));
    }

    [Test]
    public void UnresolvedOutputsArePlaceholdersInPlansAndErrorsInApply()
    {
        var task = DemoTask("apps", "api");

        var planned = ComposeDocument.Render(task.Name!, task.Options, Secret, NoLiveValues.Instance, strict: false);
        var error = Assert.Throws<TaskPlanException>(() => ComposeDocument.Render(task.Name!, task.Options, Secret, NoLiveValues.Instance, strict: true));

        Assert.That(planned.Unknown, Is.EqualTo(new[] { "api.image", "api.environment.ConnectionStrings__Crm", "api.environment.ConnectionStrings__Db", "api.environment.ConnectionStrings__Reports" }));
        Assert.That(planned.Yaml, Does.Contain("image: \"(known after apply)\""));
        Assert.That(planned.Services.Single().Image, Is.Null);
        Assert.That(error!.Message, Does.Contain("api.image is the image of dotnet.container:Api, which has no value yet"));
    }

    [Test]
    public void PlainDollarSignsAreEscapedAndOddKeysQuoted()
    {
        var options = Map(("services", Map(("web", Map(
            ("image", Text("nginx")),
            ("labels", Map(("a: b", Text("x")), ("rule", Text("Path(`/$x`)")))),
            ("environment", Map(("ON", Flag(true)), ("N", Number(3)))))))));

        var files = ComposeDocument.Render("My App!", options, Secret, NoLiveValues.Instance, strict: true);

        Assert.That(files.Project, Is.EqualTo("my-app"));
        Assert.That(files.Yaml, Does.Contain("\"a: b\": \"x\"").And.Contain("rule: \"Path(`/$$x`)\"").And.Contain("\"ON\": \"true\"").And.Contain("\"N\": \"3\""));
    }

    [Test]
    public void FlattenReadsBackWhatWriteProduced()
    {
        var task = DemoTask("apps", "api");
        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, DemoLive, strict: true);

        var entries = ComposeYaml.Flatten(files.Yaml)!;

        Assert.That(entries.Single(entry => entry.Path.SequenceEqual(new[] { "services", "api", "labels", "traefik.http.routers.api-snappy-no.rule" })).Value, Is.EqualTo("Host(`api.snappy.no`)"));
        Assert.That(entries.Single(entry => entry.Path.SequenceEqual(new[] { "services", "api", "networks", "1" })).Value, Is.EqualTo("db"));
        Assert.That(entries.Single(entry => entry.Path.SequenceEqual(new[] { "networks", "edge", "external" })).Value, Is.EqualTo("true"));
    }

    [Test]
    public void SummaryNamesImageChangesAndChangedFields()
    {
        var task = DemoTask("apps", "api");
        var before = ComposeDocument.Render(task.Name!, task.Options, Secret, DemoLive, strict: true).Yaml;
        var after = before.Replace("api:0123abc", "api:4567def").Replace("websecure", "web");

        var changes = ComposeSummary.Yaml(before, after)!;

        Assert.That(changes.Select(change => $"{change.Marker.Symbol()} {change.Text}"),
            Is.EqualTo(new[] { "~ service api: image registry.digitalocean.com/snappy/api:0123abc -> registry.digitalocean.com/snappy/api:4567def; labels changed" }));
        Assert.That(ComposeSummary.Env("A='1'\nB='2'\n", "A='1'\nB='3'\nC='4'\n"), Is.EqualTo("+C ~B"));
    }
}
