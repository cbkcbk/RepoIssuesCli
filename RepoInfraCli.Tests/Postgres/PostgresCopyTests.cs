using System.Text.Json;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Hosts;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Postgres;

public class PostgresCopyTests
{
    const string Prelude = """
                           local digitalocean = require("@infra/digitalocean")
                           local compose = require("@infra/compose")
                           local postgres = require("@infra/postgres")
                           local dotnet = require("@infra/dotnet")
                           local secret = infra.secret
                           local net = compose.network "db"
                           local shop = postgres.database "shop" { version = "17", password = secret "pg/shop", network = net, data = "/srv/pg/shop" }
                           local stage = postgres.database "stage" { version = "17", password = secret "pg/stage", network = net, data = "/srv/pg/stage" }
                           local box = infra.host "box" { address = "10.0.0.5", user = "root" }
                           local function data(h)
                             h:compose "data" { services = { shop = postgres.server(shop), stage = postgres.server(stage) } }
                           end
                           local function container(project)
                             return dotnet.container { project = project, registry = "registry.example.com/team", tag = "v1" }
                           end

                           """;

    const string Cloud = """
                         local cloud = digitalocean.provider { token = secret "do/token" }
                         local main = cloud.database_cluster "main" { engine = "pg", version = "17", size = "db-s-1vcpu-1gb", region = "fra1", protect = true }
                         local other = cloud.database_cluster "other" { engine = "pg", version = "16", size = "db-s-1vcpu-1gb", region = "fra1" }
                         local legacy = cloud.existing.database_cluster "legacy"
                         local reports = main.database "reports" {}
                         local otherReports = other.database "reports" {}
                         local crm = legacy.database "crm"

                         """;

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

    IReadOnlyList<string> Errors(string script)
    {
        repo.WriteScript(script);
        return [.. repo.Evaluate().Errors.Select(error => error.Message)];
    }

    [Test]
    public void CopyIsRecordedWithItsScrubFileAndMigrationsApp()
    {
        repo.WriteFile(".repo-infra/scrub/stage.sql", "update users set email = 'x';");
        var graph = Evaluate(Prelude + """
                                       local api = infra.app "api" { build = container("Api/Api.csproj"), deploy = function(h, image) end }
                                       postgres.copy(shop, stage) { scrub = "scrub/stage.sql", migrate = api }
                                       infra.play "p" { hosts = { box }, roles = { data } }
                                       """);

        var copy = graph.Copies.Single();

        Assert.That(copy.From, Is.EqualTo(PostgresServices.DatabaseId("shop")));
        Assert.That(copy.To, Is.EqualTo(PostgresServices.DatabaseId("stage")));
        Assert.That(copy.Scrub, Is.EqualTo("scrub/stage.sql"));
        Assert.That(copy.Migrate, Is.EqualTo(NodeId.Of(NodeKinds.App, "api")));
        Assert.That(copy.Location?.Line, Is.GreaterThan(0));
    }

    [Test]
    public void CopyValidationNamesWhatIsWrong()
    {
        var errors = Errors(Prelude + Cloud + """
                                              postgres.copy(shop, shop) {}
                                              postgres.copy(stage, shop) { scrub = "missing.sql" }
                                              postgres.copy(crm, stage) {}
                                              postgres.copy(reports, otherReports) {}
                                              postgres.copy(shop, "stage") {}
                                              infra.play "p" { hosts = { box }, roles = { data } }
                                              """);

        Assert.That(errors, Has.Some.EqualTo("postgres.copy \"shop\" to \"shop\": copies a database onto itself"));
        Assert.That(errors, Has.Some.EqualTo("postgres.copy \"stage\" to \"shop\": scrub file \"missing.sql\" does not exist in .repo-infra/"));
        Assert.That(errors, Has.Some.Contains("the postgres version of \"crm\" is unknown"));
        Assert.That(errors, Has.Some.Contains("a postgres 17 dump can't be restored into \"reports\", which runs postgres 16"));
        Assert.That(errors, Has.Some.StartsWith("postgres.copy to: ").And.Some.Contains("must be a Database"));
    }

    [Test]
    public void CopyingIntoTheSameDatabaseTwiceAndCopyingAnUnservedDatabaseFailValidation()
    {
        var errors = Errors(Prelude + """
                                      local lonely = postgres.database "lonely" { version = "17", password = secret "pg/lonely", network = net, data = "/srv/pg/lonely" }
                                      postgres.copy(shop, stage) {}
                                      postgres.copy(lonely, stage) {}
                                      infra.play "p" { hosts = { box }, roles = { data } }
                                      """);

        Assert.That(errors, Has.Some.StartsWith("postgres.copy writes \"stage\" 2 times"));
        Assert.That(errors, Has.Some.EqualTo("postgres.database \"lonely\" is used but no postgres.server runs it; add postgres.server(db) to an h:compose services table"));
    }

    [Test]
    public void CopiesArePlannedAsRunStepsBetweenBuildsAndPlays()
    {
        var graph = Evaluate(Prelude + """
                                       local api = infra.app "api" { build = container("Api/Api.csproj"), deploy = function(h, image) end }
                                       postgres.copy(shop, stage) { migrate = api }
                                       infra.play "p" { hosts = { box }, roles = { data } }
                                       """);
        var build = new BuildPlan(new BuildNode(new NodeId("dotnet.container:Api"), "dotnet.container", MapValue.Empty, null), "dotnet.container Api", ["api"], null, [Change.Add("build and push api:v1")]);
        var plan = new InfraPlan([], [new PlayPlan("p", [])], [], true) { Builds = [build], Copies = CopySteps.Plan(graph, PlanFilter.All) };
        var console = new TestConsole().Width(250);

        PlanText.Write(console, plan);

        var lines = console.Output.Split('\n').Select(line => line.TrimEnd()).ToList();
        Assert.That(lines, Does.Contain("Copies").And.Contain("  ! postgres.copy shop -> stage: replace stage with a copy of shop (migrate with api)"));
        Assert.That(lines.IndexOf("Builds"), Is.LessThan(lines.IndexOf("Copies")));
        Assert.That(lines.IndexOf("Copies"), Is.LessThan(lines.IndexOf("Plays")));
        Assert.That(console.Output, Does.Contain("1 to add, 0 to change, 0 to remove, 1 to run."));
        Assert.That(plan with { Builds = [] }, Has.Property(nameof(InfraPlan.HasDrift)).False.And.Property(nameof(InfraPlan.HasActions)).True);

        using var json = JsonDocument.Parse(PlanJson.Serialize(PlanView.From(plan)));
        var copy = json.RootElement.GetProperty("copies")[0];
        Assert.That(copy.GetProperty("source").GetString(), Is.EqualTo("shop"));
        Assert.That(copy.GetProperty("target").GetString(), Is.EqualTo("stage"));
        Assert.That(copy.GetProperty("migrate").GetString(), Is.EqualTo("api"));
        Assert.That(copy.GetProperty("changes")[0].GetProperty("marker").GetString(), Is.EqualTo("!"));
    }

    [Test]
    public void FilteredRunsLeaveCopiesOut()
    {
        var graph = Evaluate(Prelude + """
                                       postgres.copy(shop, stage) {}
                                       infra.play "p" { hosts = { box }, roles = { data } }
                                       """);

        Assert.That(CopySteps.Plan(graph, PlanFilter.All), Has.Count.EqualTo(1));
        Assert.That(CopySteps.Plan(graph, new PlanFilter(["p"], [], false)), Is.Empty);
        Assert.That(CopySteps.Plan(graph, new PlanFilter([], ["box"], false)), Is.Empty);
        Assert.That(CopySteps.Plan(graph, PlanFilter.All with { ResourcesOnly = true }), Is.Empty);
        Assert.That(CopySteps.Plan(graph, new PlanFilter([], [], true)), Is.Empty);
    }

    [Test]
    public void ProtectedTargetsAreClusterProtectionAndExports()
    {
        var graph = Evaluate(Prelude + Cloud + """
                                               infra.play "p" { hosts = { box }, roles = { data } }
                                               return { databases = { stage = stage } }
                                               """);

        Assert.That(CopyRules.IsProtected(graph, Find(graph, "main/reports")), Is.True);
        Assert.That(CopyRules.IsProtected(graph, Find(graph, "other/reports")), Is.False);
        Assert.That(CopyRules.IsProtected(graph, Find(graph, "stage")), Is.True);
        Assert.That(CopyRules.IsProtected(graph, Find(graph, "shop")), Is.False);
    }

    static NodeId Find(InfraGraph graph, string name) => DeclaredDatabases.Find(graph, name).Id;

    [Test]
    public void DatabaseNamesResolveAndClustersOrServersAreRejectedWithTheirDatabases()
    {
        var graph = Evaluate(Prelude + Cloud + """
                                               infra.play "p" { hosts = { box }, roles = { data } }
                                               """);

        Assert.That(DeclaredDatabases.Find(graph, "crm"), Has.Property(nameof(DeclaredDatabase.Managed)).True.And.Property(nameof(DeclaredDatabase.Major)).Null);
        Assert.That(DeclaredDatabases.Find(graph, "shop"), Has.Property(nameof(DeclaredDatabase.Managed)).False.And.Property(nameof(DeclaredDatabase.Major)).EqualTo(17));
        Assert.That(DeclaredDatabases.Find(graph, "other/reports").Major, Is.EqualTo(16));
        Assert.That(() => DeclaredDatabases.Find(graph, "reports"), Throws.TypeOf<UsageException>().With.Message.Contains("main/reports, other/reports"));
        Assert.That(() => DeclaredDatabases.Find(graph, "main"), Throws.TypeOf<UsageException>().With.Message.EqualTo(
            "'main' is a database cluster, not a database; copy one of its databases: main/reports."));
        Assert.That(() => DeclaredDatabases.Find(graph, "nope"), Throws.TypeOf<DatabaseNotFoundException>().With.Message.Contains("Databases: shop, stage, main/reports"));
    }

    [Test]
    public void ServiceNamesThatAreNotDatabasesPointAtTheDatabase()
    {
        var graph = Evaluate(Prelude + """
                                       local function db(h)
                                         h:compose "db" { services = { pg = postgres.server(shop), stagepg = postgres.server(stage) } }
                                       end
                                       infra.play "p" { hosts = { box }, roles = { db } }
                                       """);

        Assert.That(() => DeclaredDatabases.Find(graph, "pg"), Throws.TypeOf<UsageException>().With.Message.EqualTo(
            "'pg' is a postgres.server, not a database; copy the database it runs: shop."));
    }

    [Test]
    public void MigrationsUseTheStepAppElseTheOneAppThatDeploysWithTheDatabase()
    {
        var graph = Evaluate(Prelude + """
                                       local function uses(db)
                                         return function(h, image)
                                           h:compose "app" { services = { app = { image = image, networks = { net }, environment = { DB = db.connection_string } } } }
                                         end
                                       end
                                       local api = infra.app "api" { build = container("Api/Api.csproj"), deploy = uses(stage) }
                                       local admin = infra.app "admin" { build = container("Admin/Admin.csproj"), deploy = uses(shop) }
                                       local jobs = infra.app "jobs" { build = container("Jobs/Jobs.csproj"), deploy = uses(shop) }
                                       postgres.copy(stage, shop) { migrate = jobs }
                                       infra.play "p" { hosts = { box }, roles = { data }, apps = { api, admin, jobs } }
                                       """);
        var shop = PostgresServices.DatabaseId("shop");
        var stage = PostgresServices.DatabaseId("stage");

        Assert.That(CopySteps.MigrationsApp(graph, stage, shop).Name, Is.EqualTo("jobs"));
        Assert.That(CopySteps.MigrationsApp(graph, shop, stage).Name, Is.EqualTo("api"));
        Assert.That(CopySteps.MigrationsApp(graph, stage, null).Name, Is.EqualTo("api"));
        Assert.That(() => CopySteps.MigrationsApp(graph, shop, null), Throws.TypeOf<DatabaseCopyException>().With.Message.Contains("ambiguous: admin, jobs"));
        Assert.That(CopySteps.MigrationsProject(graph, repo.Workspace, CopySteps.MigrationsApp(graph, stage, shop).Id),
            Is.EqualTo(Path.Combine(repo.Root, "Jobs", "Jobs.csproj")));
    }

    [Test]
    public void MigrationsWithoutAnyAppSayHowToPickOne()
    {
        var graph = Evaluate(Prelude + """
                                       infra.play "p" { hosts = { box }, roles = { data } }
                                       """);

        Assert.That(() => CopySteps.MigrationsApp(graph, PostgresServices.DatabaseId("shop"), null),
            Throws.TypeOf<DatabaseCopyException>().With.Message.Contains("no app deploy uses \"shop\" and no postgres.copy step names one"));
    }

    [TestCase(false, null, false, Confirmation.NotNeeded)]
    [TestCase(true, "test", false, Confirmation.Given)]
    [TestCase(true, "prod", true, Confirmation.Wrong)]
    [TestCase(true, null, true, Confirmation.Ask)]
    [TestCase(true, null, false, Confirmation.Missing)]
    public void ProtectedTargetsNeedTheTypedOwnerOrConfirm(bool isProtected, string? confirm, bool interactive, Confirmation expected) =>
        Assert.That(CopyRules.Confirm(isProtected, "test", confirm, interactive), Is.EqualTo(expected));

    [Test]
    public void SafetyRulesRefuseSameDatabaseWrongVersionAndUnscrubbedCloudCopies()
    {
        var login = new PgLogin("postgres", "pw");
        var local = new PgEndpoint("localhost", 5432, "api", login);

        Assert.That(CopyRules.SameDatabase(local, new PgEndpoint("127.0.0.1", 5432, "api", login)), Does.Contain("refusing to copy a database onto itself"));
        Assert.That(CopyRules.SameDatabase(local, local with { Database = "api_dev" }), Is.Null);
        Assert.That(CopyRules.VersionMismatch(local, 16, 17), Is.EqualTo("localhost:5432/api runs postgres 17 but is declared as version 16; fix the declared version before copying"));
        Assert.That(CopyRules.VersionMismatch(local, null, 17), Is.Null);
        Assert.That(CopyRules.VersionMismatch(local, 17, 17), Is.Null);
        Assert.That(CopyRules.ScrubRequired(true, true, false), Does.Contain("needs --scrub"));
        Assert.That(CopyRules.ScrubRequired(true, true, true), Is.Null);
        Assert.That(CopyRules.ScrubRequired(false, true, false), Is.Null);
        Assert.That(CopyRules.ScrubRequired(true, false, false), Is.Null);
    }

    [TestCase("pg_restore: error: could not execute query: ERROR:  must be owner of extension plpgsql", true)]
    [TestCase("pg_restore: error: could not execute query: ERROR:  extension \"pg_stat_statements\" already exists", true)]
    [TestCase("pg_restore: error: could not execute query: ERROR:  schema \"public\" already exists", true)]
    [TestCase("pg_restore: error: could not execute query: ERROR:  must be owner of schema public", true)]
    [TestCase("pg_restore: error: could not execute query: ERROR:  relation \"items\" already exists", false)]
    [TestCase("pg_restore: error: could not execute query: ERROR:  permission denied to create extension \"postgis\"", false)]
    [TestCase("pg_restore: error: connection to server at \"db\" failed: FATAL:  password authentication failed", false)]
    public void RestoreErrorAllowlistOnlyCoversHarmlessExtensionErrors(string line, bool harmless) =>
        Assert.That(PgRestoreErrors.Fatal($"{line}\nCommand was: COMMENT ON EXTENSION plpgsql;\npg_restore: warning: errors ignored on restore: 1\n"),
            harmless ? Is.Empty : Is.EqualTo(new[] { line }));

    [Test]
    public void EndpointsParseUrlsAndPassCredentialsOnlyAsPgVariables()
    {
        var managed = PgEndpoint.Parse("postgresql://app%40x:p%3Ass@db-main.ondigitalocean.com:25060/reports?sslmode=require");
        var local = DatabaseEndpoints.Local("api_dev", DatabaseEndpoints.DefaultLocal);

        Assert.That(managed, Has.Property(nameof(PgEndpoint.Host)).EqualTo("db-main.ondigitalocean.com").And.Property(nameof(PgEndpoint.Port)).EqualTo(25060)
            .And.Property(nameof(PgEndpoint.Database)).EqualTo("reports").And.Property(nameof(PgEndpoint.Ssl)).True);
        Assert.That(managed.Login, Is.EqualTo(new PgLogin("app@x", "p:ss")));
        Assert.That(local, Has.Property(nameof(PgEndpoint.Database)).EqualTo("api_dev").And.Property(nameof(PgEndpoint.AdminDatabase)).EqualTo("postgres")
            .And.Property(nameof(PgEndpoint.ContainerHost)).EqualTo("host.docker.internal").And.Property(nameof(PgEndpoint.SeparateAdmin)).False);
        Assert.That(local.Login, Is.EqualTo(new PgLogin("postgres", "")));
        Assert.That(managed.Environment("reports"), Is.EquivalentTo(new Dictionary<string, string>
        {
            ["PGHOST"] = "db-main.ondigitalocean.com", ["PGPORT"] = "25060", ["PGUSER"] = "app@x", ["PGPASSWORD"] = "p:ss", ["PGDATABASE"] = "reports", ["PGSSLMODE"] = "require",
        }));
        Assert.That(() => PgEndpoint.Parse("mysql://root:hunter2@db/x"), Throws.TypeOf<DatabaseCopyException>().With.Message.Not.Contains("hunter2"));
    }

    [Test]
    public void TempNamesAndContainerNamesAreDerivedFromTheTarget()
    {
        var copier = new DatabaseCopier(ProcessRunner.Instance, FixedClock.Default);

        var temp = copier.TempName("Api");

        Assert.That(temp, Is.EqualTo("Api__infra_tmp_20261031010203"));
        Assert.That(DatabaseCopier.Containers(temp), Is.EqualTo(new CopyContainers("infra-copy-api__infra_tmp_20261031010203-dump", "infra-copy-api__infra_tmp_20261031010203-restore")));
    }

    RunResult Run(IHostConnector connector, params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, services: services => services.RegisterInstance(connector)).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    [Test]
    public void DbCopyRefusesUnsafeCopiesBeforeAnyWork()
    {
        repo.WriteScript(Prelude + Cloud + """
                                           infra.play "p" { hosts = { box }, roles = { data } }
                                           return { databases = { stage = stage } }
                                           """);
        var connector = new FakeConnector();

        var itself = Run(connector, "db", "copy", "shop", "--to", "shop");
        var unscrubbed = Run(connector, "db", "copy", "main/reports", "--to", "local:reports");
        var unconfirmed = Run(connector, "db", "copy", "shop", "--to", "stage");
        var mistyped = Run(connector, "db", "copy", "shop", "--to", "stage", "--confirm", "prod");
        var cluster = Run(connector, "db", "copy", "main", "--to", "local:x");
        var missing = Run(connector, "db", "copy", "shop");

        Assert.That(itself.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(itself.Errors, Does.Contain("refusing to copy a database onto itself"));
        Assert.That(unscrubbed.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(unscrubbed.Errors, Does.Contain("needs --scrub"));
        Assert.That(unconfirmed.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(unconfirmed.Errors, Does.Contain("stage is protected; rerun with --confirm test"));
        Assert.That(mistyped.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(mistyped.Errors, Does.Contain("--confirm prod does not match the owner test"));
        Assert.That(cluster.Errors, Does.Contain("'main' is a database cluster, not a database; copy one of its databases: main/reports."));
        Assert.That(missing.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(connector.Connects, Is.Empty);
    }

    [Test]
    public void ApplyTreatsAProtectedCopyTargetLikeADestroy()
    {
        repo.WriteScript(Prelude + """
                                   postgres.copy(shop, stage) {}
                                   infra.play "p" { hosts = { box }, roles = { data } }
                                   return { databases = { stage = stage } }
                                   """);

        string[] variables = [EnvironmentSecretResolver.VariableName("pg/shop"), EnvironmentSecretResolver.VariableName("pg/stage")];
        RunResult plan, apply;
        try
        {
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable, "pw");
            plan = Run(new FakeConnector(), "plan");
            apply = Run(new FakeConnector(), "apply", "--yes");
        }
        finally
        {
            foreach (var variable in variables)
                Environment.SetEnvironmentVariable(variable, null);
        }

        Assert.That(plan.Output, Does.Contain("Copies").And.Contain("! postgres.copy shop -> stage: replace stage with a copy of shop (target is protected)"));
        Assert.That(apply.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(apply.Errors, Does.Contain("--allow-destroy"));
    }
}
