using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Tests.Features;

public class EvaluationTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    InfraDiagnostic SingleError(EvaluationResult result)
    {
        Assert.That(result.Errors.Count(), Is.EqualTo(1), string.Join("\n", result.Diagnostics));
        return result.Errors.Single();
    }

    static string Codes(EvaluationResult result) => string.Join(",", result.Diagnostics.Select(diagnostic => diagnostic.Code));

    [Test]
    public void SandboxHidesOsIoLoadstringAndEnvironmentAccess()
    {
        repo.WriteScript("""
                         assert(os == nil, "os")
                         assert(io == nil, "io")
                         assert(loadstring == nil, "loadstring")
                         assert(getfenv == nil, "getfenv")
                         assert(setfenv == nil, "setfenv")
                         assert(debug == nil, "debug")
                         assert(string.format("%d", 3) == "3")
                         assert(math.max(1, 2) == 2)
                         assert(table.concat({ "a", "b" }, ",") == "a,b")
                         """);

        var result = repo.Evaluate();

        Assert.That(result.Diagnostics, Is.Empty);
        Assert.That(result.Graph, Is.Not.Null);
    }

    [Test]
    public void RequireOutsideInfraFolderIsRejected()
    {
        repo.WriteFile("secrets.luau", "return { leaked = true }");
        repo.WriteScript("""
                         local x = require("../secrets")
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.RequireDenied));
        Assert.That(error.Message, Does.Contain("outside .repo-infra/"));
        Assert.That(error.Location, Is.EqualTo(new SourceLocation(".repo-infra/infra.luau", 3)));
    }

    [Test]
    public void OtherAliasesAreRejected()
    {
        repo.WriteScript("""
                         local x = require("@pkg/thing")
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.RequireDenied));
        Assert.That(error.Message, Does.Contain("@pkg"));
    }

    [Test]
    public void StubsCannotBeRequiredAsFiles()
    {
        repo.WriteFile(".repo-infra/.luau/@infra/init.luau", "return {}");
        repo.WriteScript("""
                         local x = require("./.luau/@infra/init")
                         """);

        Assert.That(SingleError(repo.Evaluate()).Code, Is.EqualTo(DiagnosticCodes.RequireDenied));
    }

    [Test]
    public void RelativeRequireFromNestedModuleResolvesAgainstThatModuleAndIsCached()
    {
        repo.WriteFile(".repo-infra/lib/shared.luau", """
                                                      local counter = { loads = 0 }
                                                      counter.loads += 1
                                                      return counter
                                                      """);
        repo.WriteFile(".repo-infra/lib/roles.luau", """
                                                     local shared = require("./shared")
                                                     local infra = require("@infra")
                                                     return { shared = shared, infra = infra }
                                                     """);
        repo.WriteScript("""
                         local roles = require("./lib/roles")
                         local shared = require("./lib/shared")
                         assert(roles.shared == shared, "module is cached")
                         assert(shared.loads == 1, "loaded once")
                         assert(roles.infra == infra, "@infra is shared")
                         """);

        var result = repo.Evaluate();

        Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
    }

    [Test]
    public void MissingModuleIsAScriptError()
    {
        repo.WriteScript("""
                         local x = require("./nope")
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.ScriptError));
        Assert.That(error.Location?.Line, Is.EqualTo(3));
    }

    [Test]
    public void ConcatenatingAnOutputFailsWithTheScriptLine()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         local web = provider.droplet "web" { image = "ubuntu", size = "s", region = "fra1" }
                         local url = "http://" .. web.ipv4_address
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.OutputMisuse));
        Assert.That(error.Message, Does.Contain("digitalocean.droplet:web.ipv4_address").And.Contain("(known after apply)"));
        Assert.That(error.Location, Is.EqualTo(new SourceLocation(".repo-infra/infra.luau", 6)));
    }

    [Test]
    public void IndexingAnOutputFailsAndTostringShowsKnownAfterApply()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         local web = provider.droplet "web" { image = "ubuntu", size = "s", region = "fra1" }
                         assert(tostring(web.id) == "(known after apply)", tostring(web.id))
                         local first = web.id.value
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.OutputMisuse));
        Assert.That(error.Location?.Line, Is.EqualTo(7));
    }

    [Test]
    public void UnknownFieldOnAResourceFails()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         local web = provider.droplet "web" { image = "ubuntu", size = "s", region = "fra1" }
                         local ip = web.ipv4
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Message, Does.Contain("droplet \"web\" has no field 'ipv4'"));
        Assert.That(error.Location?.Line, Is.EqualTo(6));
    }

    [Test]
    public void UnknownOptionIsReportedAtTheDeclaringLineAndEvaluationContinues()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         provider.droplet "web" {
                             image = "ubuntu", size = "s", region = "fra1",
                             sizee = "big",
                         }
                         provider.vpc "net" {}
                         """);

        var result = repo.Evaluate();

        Assert.That(Codes(result), Is.EqualTo("unknown-option,missing-option"));
        var unknown = result.Errors.First();
        Assert.That(unknown.Message, Does.StartWith("droplet \"web\": unknown option 'sizee'"));
        Assert.That(unknown.Location, Is.EqualTo(new SourceLocation(".repo-infra/infra.luau", 5)));
        Assert.That(result.Errors.Last().Message, Does.Contain("'region'"));
        Assert.That(result.Graph!.Resources.Select(resource => resource.Name), Is.EqualTo(new[] { "web", "net" }));
    }

    [Test]
    public void WrongPrimitiveTypesAndNestedUnknownKeysAreReported()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = "plain-text" }
                         provider.firewall "web" { inbound = { { tcp = 22, form = { "1.2.3.4/32" } } }, tags = "web" }
                         """);

        var messages = repo.Evaluate().Errors.Select(error => error.Message).ToList();

        Assert.That(messages, Has.Some.Contains("digitalocean.provider: 'token' must be Secret, got string"));
        Assert.That(messages, Has.Some.Contains("unknown option 'inbound[1].form'"));
        Assert.That(messages, Has.Some.Contains("'tags' must be { string }"));
    }

    [Test]
    public void DuplicateResourceNamesAreErrors()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         provider.vpc "net" { region = "fra1" }
                         provider.vpc "net" { region = "ams3" }
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.DuplicateName));
        Assert.That(error.Message, Is.EqualTo("vpc \"net\" is declared twice (first at .repo-infra/infra.luau:5)"));
        Assert.That(error.Location?.Line, Is.EqualTo(6));
    }

    [Test]
    public void DuplicateHostsPlaysAndAppsAreErrors()
    {
        repo.WriteScript("""
                         local a = infra.host "a" { address = "10.0.0.1", user = "root" }
                         infra.host "a" { address = "10.0.0.2", user = "root" }
                         infra.play "p" { hosts = { a } }
                         infra.play "p" { hosts = { a } }
                         """);

        var result = repo.Evaluate();

        Assert.That(Codes(result), Is.EqualTo("duplicate-name,duplicate-name"));
    }

    [Test]
    public void BecomePasswordIsRecordedOnHosts()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         local droplet = provider.droplet "web-1" { image = "ubuntu-24-04-x64", size = "s-1vcpu-1gb", region = "fra1" }
                         infra.host "build-1" { address = "10.0.0.21", user = "ubuntu", become = true, become_password = infra.secret "hosts/build-1/sudo" }
                         droplet:host { user = "deploy", become = true, become_password = infra.secret "hosts/web-1/sudo" }
                         """);

        var result = repo.Evaluate();

        Assert.That(result.Diagnostics, Is.Empty);
        Assert.That(result.Graph!.Hosts.Select(host => host.BecomePassword?.Path),
            Is.EqualTo(new[] { "hosts/build-1/sudo", "hosts/web-1/sudo" }));
        Assert.That(result.Graph.Secrets.Select(secret => secret.Path), Does.Contain("hosts/web-1/sudo"));
    }

    [Test]
    public void RepoProviderAcceptsPath()
    {
        repo.WriteScript("""
                         local repo = require("@infra/repo")
                         repo.provider { url = "git@github.com:snappy/platform-infra.git", ref = "v1", path = "infra" }
                         """);

        Assert.That(repo.Evaluate().Diagnostics, Is.Empty);
    }

    [Test]
    public void ProtectOnNamePrefixKindsIsAnError()
    {
        repo.WriteScript("""
                         local digitalocean = require("@infra/digitalocean")
                         local provider = digitalocean.provider { token = infra.secret "do/token" }
                         provider.firewall "web" { protect = true }
                         provider.vpc "net" { region = "fra1", protect = true }
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.ProtectUnsupported));
        Assert.That(error.Message, Does.Contain("firewall \"web\" can't be protect").And.Contain("test-web"));
        Assert.That(error.Location?.Line, Is.EqualTo(5));
    }

    [Test]
    public void FilePathsMustStayInTheRepoOrHome()
    {
        repo.WriteScript("""
                         local inside = infra.file "keys/deploy.pub"
                         local home = infra.file "~/.ssh/deploy.pub"
                         local escaped = infra.file "../outside.pub"
                         local escapedHome = infra.file "~/../other/x"
                         """);

        var result = repo.Evaluate();

        Assert.That(result.Errors.Select(error => (error.Code, error.Location?.Line)),
            Is.EqualTo(new[] { (DiagnosticCodes.FileOutsideRepo, (int?)5), (DiagnosticCodes.FileOutsideRepo, (int?)6) }));
    }

    [Test]
    public void MissingProjectIsAnError()
    {
        repo.WriteInfra("local infra = require(\"@infra\")\n");

        var result = repo.Evaluate();

        Assert.That(Codes(result), Is.EqualTo(DiagnosticCodes.MissingProject));
        Assert.That(result.Graph, Is.Null);
    }

    [Test]
    public void SecondProjectAndInvalidProjectNamesAreErrors()
    {
        repo.WriteInfra("""
                        local infra = require("@infra")
                        infra.project "Snappy App" {}
                        infra.project "other" {}
                        """);

        Assert.That(Codes(repo.Evaluate()), Is.EqualTo("duplicate-project,invalid-name"));
    }

    [Test]
    public void TasksOutsideAPlayAreErrors()
    {
        repo.WriteScript("""
                         local a = infra.host "a" { address = "10.0.0.1", user = "root" }
                         a:apt { packages = { "curl" } }
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Code, Is.EqualTo(DiagnosticCodes.TaskOutsidePlay));
        Assert.That(error.Location?.Line, Is.EqualTo(4));
    }

    [Test]
    public void PlaysRunRolesPerHostAndRecordTasks()
    {
        repo.WriteScript("""
                         local a = infra.host "a" { address = "10.0.0.1", user = "root" }
                         local b = infra.host "b" { address = "10.0.0.2", user = "ubuntu", become = true, port = 2222 }

                         local function base(h: infra.Host)
                             h:apt { packages = { "curl" } }
                             local restart = h:handler { service = "ssh", state = "restarted" }
                             h:file "/etc/motd" { content = "hi", notify = restart }
                             h:shell "echo done"
                         end

                         infra.play "all" { hosts = { a, b }, roles = { base } }
                         """);

        var result = repo.Evaluate();
        var graph = result.Graph!;
        var play = graph.Plays.Single();

        Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
        Assert.That(play.Roles, Is.EqualTo(new[] { "base" }));
        Assert.That(play.Runs.Select(run => (graph.Host(run.Host)!.Name, run.Role, run.Tasks.Count)),
            Is.EqualTo(new[] { ("a", "base", 4), ("b", "base", 4) }));
        var tasks = play.Runs[0].Tasks;
        Assert.That(tasks.Select(task => task.Kind), Is.EqualTo(new[] { "apt", "handler", "file", "shell" }));
        Assert.That(tasks[2].Name, Is.EqualTo("/etc/motd"));
        Assert.That(tasks[2].Notifies, Is.EqualTo(tasks[1].Id));
        Assert.That(tasks[3].Options.GetString("command"), Is.EqualTo("echo done"));
        Assert.That(tasks[0].Location, Is.EqualTo(new SourceLocation(".repo-infra/infra.luau", 7)));
        Assert.That(graph.Hosts[1], Is.EqualTo(new HostNode(NodeId.Of("host", "b"), "b", new StringValue("10.0.0.2"), "ubuntu", 2222, true, null, null, false, null,
            new SourceLocation(".repo-infra/infra.luau", 4))));
    }

    [Test]
    public void ReferenceModeRecordsPlaysWithoutRunningRoles()
    {
        repo.WriteScript("""
                         local a = infra.host "a" { address = "10.0.0.1", user = "root" }
                         local function base(h: infra.Host)
                             error("roles must not run in reference mode")
                         end
                         infra.play "all" { hosts = { a }, roles = { base } }
                         """);

        var result = repo.Evaluate(mode: EvaluationMode.Reference);

        Assert.That(result.Diagnostics, Is.Empty, string.Join("\n", result.Diagnostics));
        Assert.That(result.Graph!.Plays.Single().Runs, Is.Empty);
        Assert.That(result.Graph.Plays.Single().Hosts, Is.EqualTo(new[] { NodeId.Of("host", "a") }));
    }

    [Test]
    public void RoleErrorsCarryTheRoleLine()
    {
        repo.WriteScript("""
                         local a = infra.host "a" { address = "10.0.0.1", user = "root" }
                         local function base(h: infra.Host)
                             h:service "ssh" { state = "bouncing" }
                         end
                         infra.play "all" { hosts = { a }, roles = { base } }
                         """);

        var error = SingleError(repo.Evaluate());

        Assert.That(error.Message, Does.Contain("h:service \"ssh\": 'state' must be"));
        Assert.That(error.Location?.Line, Is.EqualTo(5));
    }

    [Test]
    public void UndefinedPlayHostIsAnError()
    {
        repo.WriteScript("""
                         infra.play "all" { hosts = { { name = "ghost" } } }
                         """);

        var messages = repo.Evaluate().Errors.Select(error => error.Message);

        Assert.That(messages, Has.Some.Contains("'hosts[1]' must be a Host").And.Some.Contains("play \"all\" has no hosts"));
    }

    [Test]
    public void GitAndEnvAreExposed()
    {
        repo.WriteScript("""
                         assert(infra.git.sha == "", "sha outside git")
                         assert(infra.git.branch == "", "branch outside git")
                         assert(infra.env == nil, "no env")
                         """);

        Assert.That(repo.Evaluate().Diagnostics, Is.Empty);
    }

    [Test]
    public void GitShaIsReadFromTheRepository()
    {
        RepoCli.Core.Features.Repo.Git.Run(repo.Root, "init", "-q");
        RepoCli.Core.Features.Repo.Git.Run(repo.Root, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "x");
        var sha = RepoCli.Core.Features.Repo.Git.Run(repo.Root, "rev-parse", "HEAD")!.Output.Trim();
        repo.WriteScript($"""
                          assert(infra.git.sha == "{sha}", infra.git.sha)
                          """);

        Assert.That(repo.Evaluate().Diagnostics, Is.Empty);
    }

    [Test]
    public void SecretsAreRecordedWithoutResolution()
    {
        repo.WriteScript("""
                         local a = infra.secret "app/token"
                         local b = infra.secret "app/token"
                         assert(tostring(a) == "(secret)")
                         local c = infra.secret "db/password"
                         """);

        var graph = repo.Evaluate().Graph!;

        Assert.That(graph.Secrets, Is.EqualTo(new[] { new SecretRef("app/token"), new SecretRef("db/password") }));
    }

    [Test]
    public void ConcatenatingASecretFails()
    {
        repo.WriteScript("""
                         local token = "Bearer " .. infra.secret "app/token"
                         """);

        Assert.That(SingleError(repo.Evaluate()).Code, Is.EqualTo(DiagnosticCodes.OutputMisuse));
    }

    [Test]
    public void PrintGoesToTheLogNotStdout()
    {
        repo.WriteScript("""
                         print("hello", 42)
                         """);

        Assert.That(repo.Evaluate().Diagnostics, Is.Empty);
    }
}