using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
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

        var healthTest = ComposeYaml.Quote(ComposeHealth.HttpTest("http://localhost:8080/health").Replace("$", "$$"));
        Assert.That(files.Yaml, Is.EqualTo($$"""
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
                                                 healthcheck:
                                                   test:
                                                     - "CMD-SHELL"
                                                     - {{healthTest}}
                                                   interval: "5s"
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
        Assert.That(files.Services.Single(), Is.EqualTo(new ComposeService("api", Image)));
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
    public void DemoPostgresRendersTheServerAndItsBackupSidecar()
    {
        var task = DemoTask("data", "postgres");
        var live = new SelfHostedDatabases(new StaticLiveValues(("digitalocean.spaces_bucket:snappy-backups", "endpoint", "https://fra1.digitaloceanspaces.com")),
            graph, SecretLookup.Unavailable(new InfraWorkspace(Path.GetTempPath())));

        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, live, strict: true);

        var mounts = string.Concat(BackupScript.Commands.Select(command => $"      - \"{BackupScript.HostPath}:/usr/local/bin/{command}:ro\"\n"));
        Assert.That(files.Yaml, Is.EqualTo($$"""
                                             name: "postgres"
                                             services:
                                               backup:
                                                 image: "postgres:17"
                                                 restart: "unless-stopped"
                                                 command:
                                                   - "schedule"
                                                 networks:
                                                   - "db"
                                                 volumes:
                                             {{mounts}}    environment:
                                                   BACKUP_BUCKET: "snappy-backups"
                                                   BACKUP_CREDENTIALS: "${BACKUP_BACKUP_CREDENTIALS}"
                                                   BACKUP_ENDPOINT: "https://fra1.digitaloceanspaces.com"
                                                   BACKUP_PREFIX: "postgres/api"
                                                   BACKUP_REGION: "fra1"
                                                   BACKUP_SCHEDULE: "0 3 * * *"
                                                   PGDATABASE: "api"
                                                   PGHOST: "postgres"
                                                   PGPASSWORD: "${BACKUP_PGPASSWORD}"
                                                   PGUSER: "api"
                                                 labels:
                                                   repo-infra.postgres.backup: "api"
                                                 healthcheck:
                                                   interval: "10s"
                                                   test:
                                                     - "CMD"
                                                     - "test"
                                                     - "-f"
                                                     - "/tmp/infra-backup.ready"
                                               postgres:
                                                 image: "postgres:17"
                                                 restart: "unless-stopped"
                                                 networks:
                                                   - "db"
                                                 volumes:
                                                   - "/srv/postgres/api:/var/lib/postgresql/data"
                                                 environment:
                                                   POSTGRES_DB: "api"
                                                   POSTGRES_PASSWORD: "${POSTGRES_POSTGRES_PASSWORD}"
                                                   POSTGRES_USER: "api"
                                                 labels:
                                                   repo-infra.postgres.server: "api"
                                                 healthcheck:
                                                   interval: "10s"
                                                   test:
                                                     - "CMD-SHELL"
                                                     - "pg_isready -h 127.0.0.1 -U api -d api"
                                             networks:
                                               db:
                                                 external: true

                                             """.ReplaceLineEndings("\n")));
        Assert.That(files.Env, Is.EqualTo(ComposeEnvFile.Header + """
                                                                  BACKUP_BACKUP_CREDENTIALS='value-of-spaces/backups'
                                                                  BACKUP_PGPASSWORD='value-of-postgres/api'
                                                                  POSTGRES_POSTGRES_PASSWORD='value-of-postgres/api'

                                                                  """.ReplaceLineEndings("\n")));
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
    public void HttpHealthcheckTriesCurlThenWgetThenBashTcp()
    {
        Assert.That(ComposeHealth.HttpTest("http://localhost:8080/health"), Is.EqualTo(
            """if command -v curl >/dev/null 2>&1; then curl -fsS -o /dev/null --max-time 5 'http://localhost:8080/health'; elif command -v wget >/dev/null 2>&1; then wget -q -O /dev/null -T 5 'http://localhost:8080/health'; else bash -c 'exec 3<>"/dev/tcp/$1/$2" && printf '\''GET %s HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n'\'' "$3" "$1" >&3 && read -r -t 5 status <&3 && case "$status" in "HTTP/"*" "[23]??*) exit 0;; esac; exit 1' healthcheck 'localhost' '8080' '/health'; fi"""));
        Assert.That(ComposeHealth.HttpTest("https://localhost/health?full=1"), Does.EndWith("else exit 1; fi"));
        Assert.That(ComposeHealth.HttpTest("http://localhost/x?y=1"), Does.Contain("healthcheck 'localhost' '80' '/x?y=1'"));
    }

    [Test]
    public void HttpHealthcheckKeepsItsIntervalAndTestHealthchecksPassThrough()
    {
        static MapValue Service(params (string Key, InfraValue Value)[] healthcheck) =>
            Map(("services", Map(("web", Map(("image", Text("nginx")), ("healthcheck", Map(healthcheck)))))));

        var http = ComposeDocument.Render("web", Service(("http", Text("http://localhost/")), ("interval", Text("30s")), ("retries", Number(5))), Secret, NoLiveValues.Instance, strict: true);
        var test = ComposeDocument.Render("web", Service(("test", new ListValue([Text("CMD"), Text("true")])), ("interval", Text("9s"))), Secret, NoLiveValues.Instance, strict: true);
        var both = Assert.Throws<TaskPlanException>(() =>
            ComposeDocument.Render("web", Service(("http", Text("http://localhost/")), ("test", new ListValue([Text("CMD"), Text("true")]))), Secret, NoLiveValues.Instance, strict: true));

        Assert.That(http.Yaml, Does.Contain("      test:\n        - \"CMD-SHELL\"\n        - \"if command -v curl").And.Contain("      interval: \"30s\"\n      retries: 5\n"));
        Assert.That(http.Yaml, Does.Not.Contain("\"5s\"").And.Not.Contain("http: "));
        Assert.That(test.Yaml, Does.Contain("    healthcheck:\n      interval: \"9s\"\n      test:\n        - \"CMD\"\n        - \"true\"\n"));
        Assert.That(both!.Message, Does.Contain("web.healthcheck takes http or test, not both"));
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