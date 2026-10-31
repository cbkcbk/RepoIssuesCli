using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Compose;

namespace RepoInfraCli.Tests.Postgres;

public class PostgresModuleTests
{
    const string Prelude = """
                           local digitalocean = require("@infra/digitalocean")
                           local compose = require("@infra/compose")
                           local postgres = require("@infra/postgres")
                           local secret = infra.secret
                           local cloud = digitalocean.provider { token = secret "do/token", spaces_access_key = secret "do/ak", spaces_secret_key = secret "do/sk" }
                           local bucket = cloud.spaces_bucket "backups" { region = "ams3", lifecycle = { { prefix = "db/", expire_days = 7 } } }
                           local net = compose.network "db"
                           local shop = postgres.database "shop" { version = "18", password = secret "pg/shop", network = net, data = "/srv/pg/shop", user = "owner" }
                           local box = infra.host "box" { address = "10.0.0.5", user = "root" }

                           """;

    const string Standard = Prelude + """
                                      local function data(h)
                                        h:compose "data" {
                                          services = {
                                            pg = postgres.server(shop),
                                            backup = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "15 2 * * 1-5", prefix = "/db/shop/" },
                                          },
                                        }
                                      end
                                      local function app(h)
                                        h:compose "app" { services = { web = { image = "nginx", networks = { net }, environment = { DB = shop.connection_string, DB_HOST = shop.host } } } }
                                      end
                                      infra.play "p" { hosts = { box }, roles = { data, app } }
                                      """;

    const string Endpoint = "https://ams3.digitaloceanspaces.com";

    TempRepo repo = null!;

    [SetUp]
    public void Create() => repo = new TempRepo();

    [TearDown]
    public void Delete() => repo.Dispose();

    InfraGraph Evaluate(string script)
    {
        repo.WriteScript(script);
        var result = repo.Evaluate();
        Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
        return result.Graph!;
    }

    IReadOnlyList<InfraDiagnostic> Diagnostics(string script)
    {
        repo.WriteScript(script);
        return repo.Evaluate().Diagnostics;
    }

    SelfHostedDatabases Live(InfraGraph graph) =>
        new(new StaticLiveValues(("digitalocean.spaces_bucket:backups", "endpoint", Endpoint)), graph,
            new SecretLookup(new EnvironmentSecretResolver(name => name == EnvironmentSecretResolver.VariableName("pg/shop") ? "p@ss:w'rd" : null), repo.Workspace, null, new SecretRedactor()));

    static string Secret(SecretRef secret) => $"value-of-{secret.Path}";

    static TaskNode Compose(InfraGraph graph, string name) => graph.Tasks.Single(task => task.Kind == TaskKinds.Compose && task.Name == name);

    [Test]
    public void ServerAndBackupRenderAsComposeServicesWithSecretsInTheEnvFile()
    {
        var graph = Evaluate(Standard);
        var task = Compose(graph, "data");

        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, Live(graph), strict: true);

        var mounts = string.Concat(BackupScript.Commands.Select(command => $"      - \"{BackupScript.HostPath}:/usr/local/bin/{command}:ro\"\n"));
        Assert.That(files.Yaml, Is.EqualTo($$"""
                                             name: "data"
                                             services:
                                               backup:
                                                 image: "postgres:18"
                                                 restart: "unless-stopped"
                                                 command:
                                                   - "schedule"
                                                 networks:
                                                   - "db"
                                                 volumes:
                                             {{mounts}}    environment:
                                                   BACKUP_BUCKET: "backups"
                                                   BACKUP_CREDENTIALS: "${BACKUP_BACKUP_CREDENTIALS}"
                                                   BACKUP_ENDPOINT: "{{Endpoint}}"
                                                   BACKUP_PREFIX: "db/shop"
                                                   BACKUP_REGION: "ams3"
                                                   BACKUP_SCHEDULE: "15 2 * * 1-5"
                                                   PGDATABASE: "shop"
                                                   PGHOST: "pg"
                                                   PGPASSWORD: "${BACKUP_PGPASSWORD}"
                                                   PGUSER: "owner"
                                                 labels:
                                                   repo-infra.postgres.backup: "shop"
                                                 healthcheck:
                                                   interval: "10s"
                                                   test:
                                                     - "CMD"
                                                     - "test"
                                                     - "-f"
                                                     - "/tmp/infra-backup.ready"
                                               pg:
                                                 image: "postgres:18"
                                                 restart: "unless-stopped"
                                                 networks:
                                                   - "db"
                                                 volumes:
                                                   - "/srv/pg/shop:/var/lib/postgresql"
                                                 environment:
                                                   POSTGRES_DB: "shop"
                                                   POSTGRES_PASSWORD: "${PG_POSTGRES_PASSWORD}"
                                                   POSTGRES_USER: "owner"
                                                 labels:
                                                   repo-infra.postgres.server: "shop"
                                                 healthcheck:
                                                   interval: "10s"
                                                   test:
                                                     - "CMD-SHELL"
                                                     - "pg_isready -h 127.0.0.1 -U owner -d shop"
                                             networks:
                                               db:
                                                 external: true

                                             """.ReplaceLineEndings("\n")));
        Assert.That(files.Env, Is.EqualTo(ComposeEnvFile.Header + """
                                                                  BACKUP_BACKUP_CREDENTIALS='value-of-spaces/backups'
                                                                  BACKUP_PGPASSWORD='value-of-pg/shop'
                                                                  PG_POSTGRES_PASSWORD='value-of-pg/shop'

                                                                  """.ReplaceLineEndings("\n")));
        Assert.That(files.Unknown, Is.Empty);
    }

    [Test]
    public void ConnectionStringAndHostResolveToTheServerServiceAndKeepThePasswordInTheEnvFile()
    {
        var graph = Evaluate(Standard);
        var task = Compose(graph, "app");

        var files = ComposeDocument.Render(task.Name!, task.Options, Secret, Live(graph), strict: true);

        Assert.That(files.Yaml, Does.Contain("      DB: \"${WEB_DB}\"\n      DB_HOST: \"pg\"\n").And.Not.Contain("w'rd"));
        Assert.That(files.Env, Does.Contain("WEB_DB='postgresql://owner:p%40ss%3Aw%27rd@pg:5432/shop'\n"));
        Assert.That(files.SecretValues, Does.Contain("postgresql://owner:p%40ss%3Aw%27rd@pg:5432/shop"));
    }

    [Test]
    public void OutputsOfOtherNodesFallThroughToTheInnerValues()
    {
        var graph = Evaluate(Standard);

        var live = Live(graph);

        Assert.That(live.Resolve(new OutputRef(new NodeId("digitalocean.spaces_bucket:backups"), "endpoint")), Is.EqualTo(new StringValue(Endpoint)));
        Assert.That(live.Resolve(new OutputRef(new NodeId("postgres.database:other"), "host")), Is.Null);
    }

    [Test]
    public void BackupInstallsItsScriptOnceBeforeTheComposeTask()
    {
        var graph = Evaluate(Prelude + """
                                       local other = postgres.database "other" { version = "17", password = secret "pg/other", network = net, data = "/srv/pg/other" }
                                       local function data(h)
                                         h:compose "data" {
                                           services = {
                                             pg = postgres.server(shop),
                                             pg2 = postgres.server(other),
                                             backup = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 3 * * *", prefix = "db/shop" },
                                             backup2 = postgres.backup(other) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 4 * * *", prefix = "db/other" },
                                           },
                                         }
                                       end
                                       infra.play "p" { hosts = { box }, roles = { data } }
                                       """);

        var run = graph.Play("p")!.Runs.Single();

        Assert.That(run.Role, Is.EqualTo("data"));
        Assert.That(run.Tasks.Select(task => $"{task.Kind} {task.Name}"), Is.EqualTo(new[] { $"file {BackupScript.HostPath}", "compose data" }));
        var file = run.Tasks[0].Options;
        Assert.That(file.GetString("content"), Is.EqualTo(BackupScript.Content));
        Assert.That(file.GetString("mode"), Is.EqualTo("0755"));
        Assert.That(file.GetString("owner"), Is.EqualTo("root:root"));
        Assert.That(BackupScript.Content, Does.StartWith("#!/bin/bash\n").And.Not.Contain("\r"));
        Assert.That(BackupScript.HostPath, Does.Match("^/usr/local/lib/repo-infra/postgres-backup-[0-9a-f]{12}$"));
    }

    [TestCase("main.database \"reports\" { user = \"reports\" }", "\"reports\" is a managed DigitalOcean database, which DigitalOcean backs up itself")]
    [TestCase("legacy.database \"crm\"", "\"crm\" is a managed DigitalOcean database, which DigitalOcean backs up itself")]
    public void BackupOfAManagedDatabaseFailsValidate(string database, string reason)
    {
        repo.WriteScript(Prelude + $$"""
                                     local main = cloud.database_cluster "main" { engine = "pg", version = "17", size = "db-s-1vcpu-1gb", region = "fra1" }
                                     local legacy = cloud.existing.database_cluster "legacy"
                                     local managed = {{database}}
                                     local function data(h)
                                       h:compose "data" { services = { backup = postgres.backup(managed) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 3 * * *" } } }
                                     end
                                     infra.play "p" { hosts = { box }, roles = { data } }
                                     """);

        var validate = repo.Run("validate");

        Assert.That(validate.ExitCode, Is.Not.EqualTo(0));
        Assert.That(validate.Output + validate.Errors, Does.Contain(reason)
            .And.Contain("postgres.backup only backs up self-hosted databases (postgres.database run by postgres.server)"));
    }

    [Test]
    public void BadSchedulesAndPrefixesAreErrors()
    {
        var messages = Diagnostics(Prelude + """
                                             local function data(h)
                                               h:compose "data" {
                                                 services = {
                                                   pg = postgres.server(shop),
                                                   backup = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "@daily", prefix = "db/../x" },
                                                   backup2 = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 25 * * *", prefix = "db/shop" },
                                                 },
                                               }
                                             end
                                             infra.play "p" { hosts = { box }, roles = { data } }
                                             """).Select(diagnostic => diagnostic.Message).ToList();

        Assert.That(messages, Has.Some.Contains("schedule \"@daily\" needs five fields"));
        Assert.That(messages, Has.Some.Contains("prefix \"db/../x\" must be path segments"));
        Assert.That(messages, Has.Some.Contains("the hour field \"25\" must be"));
    }

    [Test]
    public void APrefixNoLifecycleRuleCoversIsAWarning()
    {
        var diagnostics = Diagnostics(Prelude + """
                                                local function data(h)
                                                  h:compose "data" {
                                                    services = {
                                                      pg = postgres.server(shop),
                                                      backup = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 3 * * *" },
                                                    },
                                                  }
                                                end
                                                infra.play "p" { hosts = { box }, roles = { data } }
                                                """);

        var warning = diagnostics.Single();
        Assert.That(warning.Severity, Is.EqualTo(Severity.Warning));
        Assert.That(warning.Message, Does.Contain("no lifecycle rule on bucket \"backups\" covers \"postgres/shop/\""));
    }

    [Test]
    public void ABackupOutsideARoleIsAnError()
    {
        var messages = Diagnostics(Prelude + """
                                             local service = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 3 * * *", prefix = "db/shop" }
                                             local function data(h)
                                               h:compose "data" { services = { pg = postgres.server(shop), backup = service } }
                                             end
                                             infra.play "p" { hosts = { box }, roles = { data } }
                                             """).Select(diagnostic => diagnostic.Message).ToList();

        Assert.That(messages, Is.EqualTo(new[] { "postgres.backup installs its script on the host that runs it; call it inside the role or app deploy whose h:compose uses it" }));
    }

    [Test]
    public void AUsedDatabaseNeedsExactlyOneServerOnTheBackupsHost()
    {
        var unserved = Diagnostics(Prelude + """
                                             local function app(h)
                                               h:compose "app" { services = { web = { image = "nginx", environment = { DB = shop.connection_string } } } }
                                             end
                                             infra.play "p" { hosts = { box }, roles = { app } }
                                             """).Select(diagnostic => diagnostic.Message);
        var twice = Diagnostics(Prelude + """
                                          local function data(h)
                                            h:compose "a" { services = { pg = postgres.server(shop) } }
                                            h:compose "b" { services = { pg = postgres.server(shop) } }
                                          end
                                          infra.play "p" { hosts = { box }, roles = { data } }
                                          """).Select(diagnostic => diagnostic.Message);
        var elsewhere = Diagnostics(Prelude + """
                                              local other = infra.host "other" { address = "10.0.0.6", user = "root" }
                                              local function server(h)
                                                h:compose "data" { services = { pg = postgres.server(shop) } }
                                              end
                                              local function backup(h)
                                                h:compose "backup" { services = { backup = postgres.backup(shop) { bucket = bucket, credentials = secret "spaces/backups", schedule = "0 3 * * *", prefix = "db/shop" } } }
                                              end
                                              infra.play "p" { hosts = { box }, roles = { server } }
                                              infra.play "q" { hosts = { other }, roles = { backup } }
                                              """).Select(diagnostic => diagnostic.Message);

        Assert.That(unserved, Is.EqualTo(new[] { "postgres.database \"shop\" is used but no postgres.server runs it; add postgres.server(db) to an h:compose services table" }));
        Assert.That(twice, Is.EqualTo(new[] { "postgres.database \"shop\" is run by 2 postgres.server services (host:box compose a service pg, host:box compose b service pg); run it once so its host is unambiguous" }));
        Assert.That(elsewhere, Is.EqualTo(new[] { "postgres.backup of \"shop\" runs on host:other but its postgres.server runs on host:box; put the backup on the same host so it reaches the server over the database network" }));
    }

    [Test]
    public void ServerOfVersion17MountsTheClassicDataDirectory()
    {
        var graph = Evaluate(Prelude.Replace("version = \"18\"", "version = \"17\"") + """
                                                                                  local function data(h)
                                                                                    h:compose "data" { services = { pg = postgres.server(shop) } }
                                                                                  end
                                                                                  infra.play "p" { hosts = { box }, roles = { data } }
                                                                                  """);

        var service = (MapValue)((MapValue)Compose(graph, "data").Options["services"]!)["pg"]!;

        Assert.That(service.GetString("image"), Is.EqualTo("postgres:17"));
        Assert.That(((ListValue)service["volumes"]!).Items.Single(), Is.EqualTo(new StringValue("/srv/pg/shop:/var/lib/postgresql/data")));
    }

    [TestCase("0 3 * * *")]
    [TestCase("*/15 * * * *")]
    [TestCase("0,30 8-18 1-31/2 1,6,12 0-7")]
    [TestCase("5 4\t* * sun", false)]
    [TestCase("60 * * * *", false)]
    [TestCase("* * 0 * *", false)]
    [TestCase("* * * * 8", false)]
    [TestCase("5-1 * * * *", false)]
    [TestCase("*/0 * * * *", false)]
    [TestCase("* * * *", false)]
    [TestCase("-1 * * * *", false)]
    public void CronSchedulesTakeFiveNumericFields(string schedule, bool valid = true)
    {
        Assert.That(CronSchedule.Problem(schedule) is null, Is.EqualTo(valid), CronSchedule.Problem(schedule));
    }
}
