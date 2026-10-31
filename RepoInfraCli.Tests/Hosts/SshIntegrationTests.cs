using System.Globalization;
using System.Text;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Tests.Hosts;

[Category("Docker")]
[NonParallelizable]
public class SshIntegrationTests
{
    SshdContainer sshd = null!;

    [OneTimeSetUp]
    public async Task StartContainer() => sshd = await SshdContainer.StartAsync();

    [OneTimeTearDown]
    public async Task StopContainer() => await sshd.DisposeAsync();

    HostNode Node(string user, bool become = false, string name = "box") =>
        FakeHost.Node(name, become, address: new StringValue(sshd.Address), port: sshd.Port, user: user);

    HostConnectOptions Options(bool acceptNew = false, bool trustOnFirstUse = false, string? password = null) => new()
    {
        Address = sshd.Address,
        AcceptNewHosts = acceptNew,
        TrustOnFirstUse = trustOnFirstUse,
        BecomePassword = password is null ? null : (_, _) => Task.FromResult(password),
    };

    [Test]
    public void UnknownHostKeyFailsWithoutAcceptNewHosts()
    {
        var knownHosts = sshd.KnownHostsFile("unknown");

        var error = Assert.ThrowsAsync<HostException>(() => sshd.Connector(knownHosts).ConnectAsync(Node("nopw"), Options()));

        Assert.That(error!.Message, Does.Contain("not in known_hosts").And.Contain("--accept-new-hosts"));
        Assert.That(File.ReadAllText(knownHosts), Is.Empty);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task AcceptNewHostsOrTrustOnFirstUseRecordsTheKeyThenStrictWorks(bool acceptNew, bool trustOnFirstUse)
    {
        var knownHosts = sshd.KnownHostsFile($"accept-{acceptNew}");

        await using (var first = await sshd.Connector(knownHosts).ConnectAsync(Node("nopw"), Options(acceptNew, trustOnFirstUse)))
            Assert.That(first.Facts.VersionId, Is.EqualTo("24.04"));
        await using var second = await sshd.Connector(knownHosts).ConnectAsync(Node("nopw"), Options());

        Assert.That(File.ReadAllText(knownHosts), Does.Contain(sshd.HostKeyLine.Split(' ')[1]));
        Assert.That(second.Facts.Codename, Is.EqualTo("noble"));
    }

    [Test]
    public void ChangedHostKeyAlwaysFails()
    {
        var knownHosts = sshd.KnownHostsFile("changed", sshd.OtherKeyLine);

        var error = Assert.ThrowsAsync<HostException>(() => sshd.Connector(knownHosts).ConnectAsync(Node("nopw"), Options(acceptNew: true, trustOnFirstUse: true)));

        Assert.That(error!.Message, Does.Contain("CHANGED"));
    }

    [Test]
    public async Task PasswordlessSudoBecomesRoot()
    {
        await using var connection = (HostSession)await sshd.Connector(sshd.KnownHostsFile("nopw", sshd.HostKeyLine)).ConnectAsync(Node("nopw", become: true), Options());

        var result = await connection.RunAsync("id -u");

        Assert.That(connection.Become, Is.EqualTo(BecomeMode.Passwordless));
        Assert.That(result.Stdout.Trim(), Is.EqualTo("0"));
    }

    [Test]
    public async Task SudoPasswordOverStdinBecomesRootAndKeepsStdinForTheScript()
    {
        await using var connection = (HostSession)await sshd.Connector(sshd.KnownHostsFile("pw", sshd.HostKeyLine))
            .ConnectAsync(Node("pw", become: true), Options(password: SshdContainer.SudoPassword));

        var id = await connection.RunAsync("id -u");
        var echoed = await connection.RunAsync("cat", Encoding.UTF8.GetBytes("payload-from-stdin"));
        var processes = await connection.RunAsync("ps -eo args");

        Assert.That(connection.Become, Is.EqualTo(BecomeMode.Password));
        Assert.That(id.Stdout.Trim(), Is.EqualTo("0"));
        Assert.That(echoed.Stdout, Is.EqualTo("payload-from-stdin"));
        Assert.That(processes.Stdout, Does.Not.Contain(SshdContainer.SudoPassword));
    }

    [Test]
    public void WrongSudoPasswordIsRejected()
    {
        var error = Assert.ThrowsAsync<HostException>(() => sshd.Connector(sshd.KnownHostsFile("wrong", sshd.HostKeyLine))
            .ConnectAsync(Node("pw", become: true), Options(password: "wrong")));

        Assert.That(error!.Message, Does.Contain("rejected the password"));
    }

    [Test]
    public void MissingSudoPasswordNamesTheSecret()
    {
        var error = Assert.ThrowsAsync<HostException>(() => sshd.Connector(sshd.KnownHostsFile("missing", sshd.HostKeyLine))
            .ConnectAsync(Node("pw", become: true, name: "build-1"), Options()));

        Assert.That(error!.Message, Does.Contain("hosts/build-1/sudo"));
    }

    [Test]
    public async Task UploadAndReadFileRoundTripWithModeAndOwner()
    {
        await using var connection = await sshd.Connector(sshd.KnownHostsFile("upload", sshd.HostKeyLine)).ConnectAsync(Node("nopw", become: true), Options());

        await connection.UploadAsync("/opt/infra-test/blob.bin", new byte[] { 0, 1, 2, 255 }, "0600", "nopw");
        var back = await connection.ReadFileAsync("/opt/infra-test/blob.bin");
        var stat = await connection.RunAsync("stat -c '%a %U' /opt/infra-test/blob.bin");
        var missing = await connection.ReadFileAsync("/opt/infra-test/none");

        Assert.That(back, Is.EqualTo(new byte[] { 0, 1, 2, 255 }));
        Assert.That(stat.Stdout.Trim(), Is.EqualTo("600 nopw"));
        Assert.That(missing, Is.Null);
    }

    [Test]
    public async Task PlanApplyPlanConvergesToNoChanges()
    {
        using var repo = new TempRepo();
        repo.WriteScript($$"""
            local box = infra.host "box" { address = "{{sshd.Address}}", user = "nopw", port = {{sshd.Port.ToString(CultureInfo.InvariantCulture)}}, become = true }

            local function role(h)
              h:apt { update = true, packages = { "tree" } }
              h:user "deploy" { shell = "/bin/bash", groups = { "adm" }, authorized_keys = { "{{sshd.PublicKey}}" } }
              local restart = h:handler { service = "cron", state = "restarted" }
              h:file "/etc/infra-test/app.conf" { content = "a=1\n", mode = "0640", owner = "root:adm", notify = restart }
              h:service "cron" { enabled = true, state = "started" }
              h:ufw { default = "deny", allow = { { tcp = 22 }, { tcp = 443, from = { "10.0.0.0/8" } } } }
            end

            infra.play "box" { hosts = { box }, roles = { role } }
            """);
        var graph = repo.Evaluate().Graph!;
        var planner = new Planner(sshd.Connector(sshd.KnownHostsFile("converge", sshd.HostKeyLine)));
        var options = new PlanOptions { Workspace = repo.Workspace, Log = InfraLog.Silent() };

        InfraPlan first;
        ApplyResult applied;
        await using (var run = await planner.PlanAsync(graph, options))
        {
            first = run.Plan;
            applied = await new Applier().ApplyAsync(run, InfraLog.Silent());
        }
        await using var second = await planner.PlanAsync(graph, options);

        Assert.That(first.Problems, Is.Empty);
        var planned = first.Tasks.Where(task => task.Changes.Count > 0).Select(task => task.Label).ToList();
        Assert.That(planned, Is.EqualTo(new[] { "apt", "user deploy", "handler cron", "file /etc/infra-test/app.conf", "service cron", "ufw" }));
        Assert.That(applied.Succeeded, Is.True, applied.Failure?.Error);
        Assert.That(applied.Steps.Select(step => step.Label), Does.Contain("handler cron"));
        Assert.That(second.Plan.Problems, Is.Empty);
        Assert.That(second.Plan.Tasks.SelectMany(task => task.Changes).Select(change => change.Text), Is.Empty);
        Assert.That(second.Plan.HasDrift, Is.False);
        Assert.That(await sshd.ExecAsRootAsync("stat", "-c", "%a %U:%G", "/etc/infra-test/app.conf"), Is.EqualTo("640 root:adm\n"));
        Assert.That(await sshd.ExecAsRootAsync("ufw", "status"), Does.Contain("Status: active").And.Contain("443/tcp"));
    }
}
