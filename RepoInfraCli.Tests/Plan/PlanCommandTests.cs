using System.Text.Json;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Tests.Hosts;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Plan;

public class PlanCommandTests
{
    const string TwoHosts = """
                            local web = infra.host "web" { address = "10.0.0.1", user = "root" }
                            local build = infra.host "build" { address = "10.0.0.2", user = "ubuntu", become = true }

                            local function base(h)
                              h:apt { packages = { "curl" } }
                              local restart = h:handler { service = "ssh", state = "restarted" }
                              h:file "/etc/ssh/sshd_config.d/10.conf" { content = "PasswordAuthentication no\n", notify = restart }
                              h:shell "echo hi"
                            end

                            local function app(h)
                              h:compose "api" { services = { api = { image = "nginx" } } }
                            end

                            infra.play "base" { hosts = { web, build }, roles = { base } }
                            infra.play "apps" { hosts = { web }, roles = { app } }
                            """;

    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    static FakeConnector Fresh() => new FakeConnector()
        .Host("web", host => host.On("dpkg-query", "pkg curl un \n"))
        .Host("build", host => host.On("dpkg-query", "pkg curl ii \n").On("sha256sum", $"mode 644\nowner root:root\nsha256 {Hash("PasswordAuthentication no\n")}\n"));

    static string Hash(string text) => FileTask.Sha256(System.Text.Encoding.UTF8.GetBytes(text));

    RunResult Run(IHostConnector connector, params string[] args) => Run(connector, new TestConsole().Width(250), args);

    RunResult Run(IHostConnector connector, TestConsole output, params string[] args)
    {
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, services: services => services.RegisterInstance(connector)).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    [Test]
    public void PlanShowsResourcesThenPlaysWithMarkers()
    {
        repo.WriteScript(TwoHosts);

        var result = Run(Fresh(), "plan");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        var lines = result.Output.Split('\n').Select(line => line.TrimEnd()).ToList();
        Assert.That(lines, Does.Contain("Resources").And.Contain("  no changes").And.Contain("Plays").And.Contain("  base"));
        Assert.That(lines, Does.Contain("    web root@10.0.0.1").And.Contain("        + apt: install curl").And.Contain("        + file /etc/ssh/sshd_config.d/10.conf: create (26 bytes)"));
        Assert.That(lines, Does.Contain("        ~ handler ssh: restarted (notified)").And.Contain("        ! shell: echo hi"));
        Assert.That(lines, Does.Contain("    build ubuntu@10.0.0.2"));
        Assert.That(lines, Does.Contain("        + compose api: create /opt/api/compose.yaml and .env (service(s) api; no secrets)"));
        Assert.That(result.Output, Does.Contain("Plan: 3 to add, 1 to change, 0 to remove, 2 to run."));
        Assert.That(lines.IndexOf("Resources"), Is.LessThan(lines.IndexOf("Plays")));
    }

    [Test]
    public void HandlerIsOnlyPlannedWhenANotifierChanges()
    {
        repo.WriteScript(TwoHosts);

        var output = Run(Fresh(), "plan", "--host", "build").Output;

        Assert.That(output, Does.Not.Contain("handler ssh").And.Not.Contain("    web "));
        Assert.That(output, Does.Contain("! shell: echo hi"));
    }

    [Test]
    public void FiltersSelectPlaysAndHostsAndUnknownNamesAreNotFound()
    {
        repo.WriteScript(TwoHosts);

        var apps = Run(Fresh(), "plan", "--play", "apps", "--hosts-only");
        var missingPlay = Run(Fresh(), "plan", "--play", "nope");
        var missingHost = Run(Fresh(), "plan", "--host", "nope");

        Assert.That(apps.Output, Does.Not.Contain("Resources").And.Not.Contain("  base").And.Contain("  apps"));
        Assert.That(missingPlay.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(missingPlay.Errors, Does.Contain("No play named 'nope'").And.Contain("base, apps"));
        Assert.That(missingHost.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
    }

    [Test]
    public void ExitCodeFlagReturnsSixOnDriftOnly()
    {
        repo.WriteScript(TwoHosts);
        var shellOnly = new FakeConnector().Host("build", host => host.On("dpkg-query", "pkg curl ii \n").On("sha256sum", $"mode 644\nowner root:root\nsha256 {Hash("PasswordAuthentication no\n")}\n"));

        var drift = Run(Fresh(), "plan", "--exit-code");
        var noDrift = Run(shellOnly, "plan", "--exit-code", "--host", "build");

        Assert.That(drift.ExitCode, Is.EqualTo(InfraExitCodes.Drift));
        Assert.That(noDrift.ExitCode, Is.EqualTo(InfraExitCodes.Success), noDrift.Output);
    }

    [Test]
    public void ConnectionFailuresAreReportedPerHostAndExitOne()
    {
        repo.WriteScript(TwoHosts);

        var result = Run(Fresh().Fail("build", "host 'build' runs debian 12; only Ubuntu 22.04 and 24.04 are supported"), "plan");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("build error: host 'build' runs debian 12").And.Contain("+ apt: install curl"));
    }

    [Test]
    public void JsonPlanHasChangesSummaryResourcesPlaysAndProblems()
    {
        repo.WriteScript(TwoHosts);

        var result = Run(Fresh(), "plan", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(json.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "changes", "summary", "resources", "plays", "problems" }));
        Assert.That(json.GetProperty("changes").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("summary").GetProperty("add").GetInt32(), Is.EqualTo(3));
        var web = json.GetProperty("plays")[0].GetProperty("hosts")[0];
        Assert.That(web.GetProperty("status").GetString(), Is.EqualTo("planned"));
        var apt = web.GetProperty("roles")[0].GetProperty("tasks")[0];
        Assert.That(apt.GetProperty("kind").GetString(), Is.EqualTo("apt"));
        Assert.That(apt.GetProperty("file").GetString(), Is.EqualTo(".repo-infra/infra.luau"));
        Assert.That(apt.GetProperty("changes")[0].GetProperty("marker").GetString(), Is.EqualTo("+"));
        Assert.That(apt.GetProperty("changes")[0].GetProperty("action").GetString(), Is.EqualTo("add"));
    }

    [Test]
    public void DropletHostsWaitForTheirAddressAndUnfetchedHostsFailThePlan()
    {
        repo.CopyDemo();
        var connector = new FakeConnector();

        var edge = Run(connector, "plan", "--play", "edge", "--hosts-only");
        var nas = Run(connector, "plan", "--play", "nas", "--hosts-only");

        Assert.That(edge.ExitCode, Is.Zero, edge.Output + edge.Errors);
        Assert.That(edge.Output, Does.Contain("web-1 (address known after apply; skipped)"));
        Assert.That(nas.ExitCode, Is.EqualTo(InfraExitCodes.Invalid), nas.Output + nas.Errors);
        Assert.That(nas.Output, Does.Contain("nas (from a repo.provider that could not be fetched)"));
        Assert.That(connector.Connects, Is.Empty);
    }

    [Test]
    public void BuildHostOfTheDemoPlansButTheUnfetchedRoleFailsThePlan()
    {
        repo.CopyDemo();
        var connector = new FakeConnector();

        var result = Run(connector, "plan", "--play", "build", "--hosts-only");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid), result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("build-1 ubuntu@10.0.0.21")
            .And.Contain("platform-infra/node_exporter (role from a repo.provider that could not be fetched)")
            .And.Contain("+ apt: install ufw"));
        Assert.That(connector.Connects.Single().Options.Address, Is.EqualTo("10.0.0.21"));
    }

    [Test]
    public void AcceptNewHostsIsPassedToTheConnector()
    {
        repo.WriteScript(TwoHosts);
        var connector = Fresh();

        Run(connector, "plan", "--accept-new-hosts");

        Assert.That(connector.Connects.All(connect => connect.Options.AcceptNewHosts), Is.True);
        Assert.That(connector.Connects.Any(connect => connect.Options.TrustOnFirstUse), Is.False);
    }

    [Test]
    public void ApplyWithoutTerminalOrYesIsAUsageError()
    {
        repo.WriteScript(TwoHosts);
        var connector = Fresh();

        var result = Run(connector, "apply");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--yes"));
        Assert.That(connector.Opened["web"].Uploads, Is.Empty);
    }

    [Test]
    public void ApplyRunsChangedTasksShellsAndNotifiedHandlers()
    {
        repo.WriteScript(TwoHosts);
        var connector = Fresh();

        var result = Run(connector, "apply", "--yes", "--play", "base");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        var web = connector.Opened["web"];
        var build = connector.Opened["build"];
        Assert.That(web.Scripts, Has.Some.Contains("install 'curl'").And.Some.EqualTo("echo hi").And.Some.EqualTo("systemctl restart 'ssh'"));
        Assert.That(web.Uploads.Single().Path, Is.EqualTo("/etc/ssh/sshd_config.d/10.conf"));
        Assert.That(build.Scripts, Has.None.Contains("install 'curl'").And.None.Contains("systemctl restart"));
        Assert.That(build.Scripts, Has.Some.EqualTo("echo hi"));
        Assert.That(result.Output, Does.Contain("done web base > apt").And.Contain("Apply complete: 5 task(s) applied."));
        Assert.That(web.Disposed && build.Disposed, Is.True);
    }

    [Test]
    public void InteractiveApplyAsksBeforeChanging()
    {
        repo.WriteScript(TwoHosts);
        var connector = Fresh();
        var console = new TestConsole().Width(250).Interactive();
        console.Input.PushTextWithEnter("n");

        var result = Run(connector, console, "apply", "--host", "web");

        Assert.That(result.Output, Does.Contain("Apply these changes?").And.Contain("Apply cancelled."));
        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(connector.Opened["web"].Uploads, Is.Empty);
    }

    [Test]
    public void FailureAfterChangesIsPartiallyAppliedAndStopsLaterPlays()
    {
        repo.WriteScript(TwoHosts);
        var connector = new FakeConnector().Host("web", host => host
            .On("dpkg-query", "pkg curl un \n")
            .On("echo hi", "", exitCode: 2, stderr: "boom"));

        var result = Run(connector, "apply", "--yes", "--host", "web");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.PartiallyApplied), result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("failed web base > shell").And.Contain("boom").And.Contain("Apply failed"));
        Assert.That(connector.Opened["web"].Scripts, Has.None.EqualTo("systemctl restart 'ssh'"));
    }

    [Test]
    public void FailureBeforeAnyChangeExitsOne()
    {
        repo.WriteScript(TwoHosts);
        var connector = new FakeConnector().Host("web", host => host
            .On("dpkg-query", "pkg curl un \n")
            .On("install 'curl'", "", exitCode: 100, stderr: "E: no"));

        var result = Run(connector, "apply", "--yes", "--host", "web", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(json.GetProperty("applied").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("failure").GetProperty("task").GetString(), Is.EqualTo("apt"));
        Assert.That(json.GetProperty("failure").GetProperty("error").GetString(), Does.Contain("E: no"));
    }

    [Test]
    public void ApplyWithNothingToDoSucceedsWithoutPrompt()
    {
        repo.WriteScript("""
                         local web = infra.host "web" { address = "10.0.0.1", user = "root" }
                         infra.play "p" { hosts = { web }, roles = { function(h) h:apt { packages = { "curl" } } end } }
                         """);
        var connector = new FakeConnector().Host("web", host => host.On("dpkg-query", "pkg curl ii \n"));

        var result = Run(connector, "apply");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("No changes."));
    }

    [Test]
    public void EvaluationErrorsBlockThePlan()
    {
        repo.WriteScript("""infra.host "a" { user = "root" }""");

        var result = Run(new FakeConnector(), "plan");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("missing-option"));
    }

    [Test]
    public void BecomeWithoutPasswordFailsTheNonInteractivePlanNamingTheSecret()
    {
        repo.WriteScript(TwoHosts);
        var connector = new SudoCheckingConnector();

        var result = Run(connector, "apply", "--yes", "--host", "build");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("hosts/build/sudo"));
    }

    sealed class SudoCheckingConnector : IHostConnector
    {
        public async Task<IHostConnection> ConnectAsync(HostNode host, HostConnectOptions options, CancellationToken cancellationToken = default)
        {
            if (options.BecomePassword is null)
                throw new InvalidOperationException("the planner always passes a password provider");
            await options.BecomePassword(host, cancellationToken);
            return new FakeHost(host);
        }
    }

    [Test]
    public void PlanSeamsDefaultToNothing()
    {
        Assert.That(NoLiveValues.Instance.Resolve(new OutputRef(new("droplet:web-1"), "ipv4_address")), Is.Null);
        Assert.That(NoResourcePlanner.Instance.PlanAsync(null!, PlanFilter.All, CancellationToken.None).Result, Is.Empty);
    }
}