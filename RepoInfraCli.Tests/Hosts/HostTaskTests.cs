using System.Text;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using static RepoInfraCli.Tests.Hosts.Options;

namespace RepoInfraCli.Tests.Hosts;

public class HostTaskTests
{
    static readonly InfraWorkspace Workspace = new(Path.GetTempPath());

    static FakeHost NewHost() => new(FakeHost.Node());

    static TaskContext Context(FakeHost host, TaskNode task) => new(host, task, Workspace, NoLiveValues.Instance, SecretLookup.Unavailable(Workspace));

    static async Task<(TaskProbe Probe, string[] Changes)> Probe(IHostTask module, FakeHost host, TaskNode task)
    {
        var probe = await module.ProbeAsync(Context(host, task), CancellationToken.None);
        return (probe, [.. probe.Changes.Select(change => $"{change.Marker.Symbol()} {change.Text}")]);
    }

    static async Task<string> Apply(IHostTask module, FakeHost host, TaskNode task, TaskProbe probe)
    {
        var before = host.Scripts.Count;
        await module.ApplyAsync(Context(host, task), probe, CancellationToken.None);
        return string.Join("\n---\n", host.Scripts.Skip(before));
    }

    [Test]
    public async Task AptInstallsMissingAndRemovesAbsentPackages()
    {
        var host = NewHost().On("dpkg-query", "pkg curl ii \npkg ufw un \npkg telnet ii \n");
        var task = Task("apt", null, Map(("packages", List("curl", "ufw")), ("absent", List("telnet", "ftp"))));

        var (probe, changes) = await Probe(new AptTask(), host, task);
        var script = await Apply(new AptTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "+ install ufw", "- remove telnet" }));
        Assert.That(script, Does.Contain("install 'ufw'").And.Contain("remove 'telnet'").And.Not.Contain("'curl'"));
        Assert.That(script, Does.Contain("DEBIAN_FRONTEND=noninteractive"));
    }

    [Test]
    public async Task AptWithEverythingInstalledHasNoChanges()
    {
        var host = NewHost().On("dpkg-query", "pkg curl ii \n");

        var (probe, _) = await Probe(new AptTask(), host, Task("apt", null, Map(("packages", List("curl")), ("update", Flag(true)))));

        Assert.That(probe.Changes, Is.Empty);
    }

    [Test]
    public async Task AptUpgradeReportsUpgradablePackagesAndUpdatesFirst()
    {
        var host = NewHost().On("apt-get -s upgrade", "upgradable 3\n");
        var task = Task("apt", null, Map(("upgrade", Text("safe")), ("update", Flag(true))));

        var (probe, changes) = await Probe(new AptTask(), host, task);
        var script = await Apply(new AptTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "~ upgrade 3 package(s) (safe)" }));
        Assert.That(script.IndexOf(" update", StringComparison.Ordinal), Is.LessThan(script.IndexOf(" upgrade", StringComparison.Ordinal)));
    }

    [Test]
    public async Task AptRepoAddsKeyringAndDeb822SourceForTheHostCodename()
    {
        var host = NewHost();
        var task = Task("apt_repo", "docker", Map(("url", Text("https://download.docker.com/linux/ubuntu")), ("key", Text("https://download.docker.com/linux/ubuntu/gpg"))));

        var (probe, changes) = await Probe(new AptRepoTask(), host, task);
        var script = await Apply(new AptRepoTask(), host, task, probe);

        Assert.That(changes, Has.Length.EqualTo(2));
        Assert.That(changes[0], Does.StartWith("+ keyring /etc/apt/keyrings/docker.asc"));
        Assert.That(changes[1], Does.Contain("/etc/apt/sources.list.d/docker.sources").And.Contain("noble"));
        Assert.That(script, Does.Contain("curl -fsSL 'https://download.docker.com/linux/ubuntu/gpg'")
            .And.Contain("'Suites: noble'").And.Contain("'Components: stable'").And.Contain("\"Signed-By: $k\"").And.Contain("apt-get -q -y -o DPkg::Lock::Timeout=300 update"));
    }

    [Test]
    public async Task AptRepoWithMatchingSourceHasNoChanges()
    {
        var task = Task("apt_repo", "docker", Map(("url", Text("https://example.test/repo")), ("key", Text("https://example.test/key")), ("components", List("main"))));
        var probeHost = NewHost();
        var expected = AptRepoTask.Sources(Context(probeHost, task), "/etc/apt/keyrings/docker.gpg");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
        var host = NewHost().On("keyrings", $"keyring /etc/apt/keyrings/docker.gpg\nsources {hash}\n");

        var (probe, _) = await Probe(new AptRepoTask(), host, task);

        Assert.That(expected, Does.Contain("Components: main\n").And.Contain("Signed-By: /etc/apt/keyrings/docker.gpg\n"));
        Assert.That(probe.Changes, Is.Empty);
    }

    [Test]
    public async Task UserIsCreatedWithShellGroupsAndKeys()
    {
        var host = NewHost().On("getent passwd", "exists 0\n");
        var task = Task("user", "deploy", Map(("shell", Text("/bin/bash")), ("groups", List("docker")), ("authorized_keys", List("ssh-ed25519 AAAA deploy"))));

        var (probe, changes) = await Probe(new UserTask(), host, task);
        var script = await Apply(new UserTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "+ create (shell /bin/bash; groups docker; 1 authorized key(s))" }));
        Assert.That(script, Does.Contain("useradd -m -s '/bin/bash' 'deploy'").And.Contain("usermod -aG 'docker' 'deploy'")
            .And.Contain("grep -qxF 'ssh-ed25519 AAAA deploy'"));
    }

    [Test]
    public async Task ExistingUserOnlyAddsMissingGroupsAndKeysByDefault()
    {
        var host = NewHost().On("getent passwd", "exists 1\nshell /bin/sh\ngroups deploy adm\nprimary deploy\nkey ssh-ed25519 AAAA old\n");
        var task = Task("user", "deploy", Map(("shell", Text("/bin/bash")), ("groups", List("docker")), ("authorized_keys", List("ssh-ed25519 AAAA old", "ssh-ed25519 BBBB new"))));

        var (_, changes) = await Probe(new UserTask(), host, task);

        Assert.That(changes, Is.EqualTo(new[] { "~ shell /bin/sh -> /bin/bash", "~ groups +docker", "~ authorized_keys +1 key(s)" }));
    }

    [Test]
    public async Task UserWithAppendFalseSetsGroupsExactly()
    {
        var host = NewHost().On("getent passwd", "exists 1\nshell /bin/bash\ngroups deploy adm\nprimary deploy\n");
        var task = Task("user", "deploy", Map(("groups", List("docker")), ("append", Flag(false))));

        var (probe, changes) = await Probe(new UserTask(), host, task);
        var script = await Apply(new UserTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "~ groups +docker -adm" }));
        Assert.That(script, Does.Contain("usermod -G 'docker' 'deploy'"));
    }

    [Test]
    public async Task UserMatchingEverythingHasNoChanges()
    {
        var host = NewHost().On("getent passwd", "exists 1\nshell /bin/bash\ngroups deploy docker\nprimary deploy\nkey ssh-ed25519 AAAA k\n");
        var task = Task("user", "deploy", Map(("shell", Text("/bin/bash")), ("groups", List("docker")), ("append", Flag(true)), ("authorized_keys", List("ssh-ed25519 AAAA k"))));

        var (probe, _) = await Probe(new UserTask(), host, task);

        Assert.That(probe.Changes, Is.Empty);
    }

    [Test]
    public async Task UserKeysFromAFileRefAreRead()
    {
        var keyFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(keyFile, "# comment\nssh-ed25519 CCCC from-file\n");
        try
        {
            var host = NewHost().On("getent passwd", "exists 1\nshell /bin/bash\ngroups deploy\nprimary deploy\n");
            var task = Task("user", "deploy", Map(("authorized_keys", new ListValue([new FileRef("~/.ssh/deploy.pub", keyFile)]))));

            var (probe, changes) = await Probe(new UserTask(), host, task);
            var script = await Apply(new UserTask(), host, task, probe);

            Assert.That(changes, Is.EqualTo(new[] { "~ authorized_keys +1 key(s)" }));
            Assert.That(script, Does.Contain("'ssh-ed25519 CCCC from-file'").And.Not.Contain("comment"));
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Test]
    public async Task FileIsCreatedByUploadWithModeAndOwner()
    {
        var host = NewHost();
        var task = Task("file", "/etc/app.conf", Map(("content", Text("a=1\n")), ("mode", Text("0600")), ("owner", Text("deploy"))));

        var (probe, changes) = await Probe(new FileTask(), host, task);
        await Apply(new FileTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "+ create (4 bytes, mode 0600, owner deploy)" }));
        var upload = host.Uploads.Single();
        Assert.That(upload, Is.EqualTo(upload with { Path = "/etc/app.conf", Mode = "0600", Owner = "deploy" }));
        Assert.That(Encoding.UTF8.GetString(upload.Content), Is.EqualTo("a=1\n"));
    }

    [Test]
    public async Task FileContentIsComparedBySha256()
    {
        var hash = FileTask.Sha256(Encoding.UTF8.GetBytes("a=1\n"));
        var same = NewHost().On("sha256sum", $"mode 644\nowner root:root\nsha256 {hash}\n");
        var different = NewHost().On("sha256sum", "mode 644\nowner root:root\nsha256 0000\n");
        var task = Task("file", "/etc/app.conf", Map(("content", Text("a=1\n"))));

        var (unchanged, _) = await Probe(new FileTask(), same, task);
        var (changed, changes) = await Probe(new FileTask(), different, task);

        Assert.That(unchanged.Changes, Is.Empty);
        Assert.That(changes.Single(), Does.StartWith("~ content (sha256 0000 -> "));
        Assert.That(changed.HasDrift, Is.True);
    }

    [Test]
    public async Task FileModeAndOwnerChangesDoNotReupload()
    {
        var hash = FileTask.Sha256(Encoding.UTF8.GetBytes("x"));
        var host = NewHost().On("sha256sum", $"mode 644\nowner root:root\nsha256 {hash}\n");
        var task = Task("file", "/etc/x", Map(("content", Text("x")), ("mode", Text("0640")), ("owner", Text("root:adm"))));

        var (probe, changes) = await Probe(new FileTask(), host, task);
        var script = await Apply(new FileTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "~ mode 644 -> 0640", "~ owner root:root -> root:adm" }));
        Assert.That(host.Uploads, Is.Empty);
        Assert.That(script, Does.Contain("chmod '0640' '/etc/x'").And.Contain("chown 'root:adm' '/etc/x'"));
    }

    [Test]
    public void MissingFileWithoutContentIsAPlanError()
    {
        var task = Task("file", "/etc/x", Map(("mode", Text("0600"))));

        Assert.ThrowsAsync<TaskPlanException>(() => new FileTask().ProbeAsync(Context(NewHost(), task), CancellationToken.None));
    }

    [Test]
    public void FileModeMustBeOctal()
    {
        var task = Task("file", "/etc/x", Map(("content", Text("x")), ("mode", Text("rw-r--r--"))));

        Assert.ThrowsAsync<TaskPlanException>(() => new FileTask().ProbeAsync(Context(NewHost(), task), CancellationToken.None));
    }

    [Test]
    public void UfwRulesExpandPortsAndSources()
    {
        var rules = UfwTask.Rules(Map(("allow", new ListValue(
        [
            Map(("tcp", Number(22)), ("from", List("10.0.0.0/24", "84.212.0.0/16"))),
            Map(("tcp", Number(443))),
            Map(("udp", Text("8000:8100")), ("to", List("10.2.0.1"))),
            Map(("from", List("10.9.0.0/16"))),
        ]))));

        Assert.That(rules, Is.EqualTo(new[]
        {
            "allow from 10.0.0.0/24 to any port 22 proto tcp",
            "allow from 84.212.0.0/16 to any port 22 proto tcp",
            "allow 443/tcp",
            "allow to 10.2.0.1 port 8000:8100 proto udp",
            "allow from 10.9.0.0/16",
        }));
    }

    [Test]
    public void UfwRejectsIcmp() =>
        Assert.Throws<TaskPlanException>(() => UfwTask.Rules(Map(("allow", new ListValue([Map(("icmp", Flag(true)))])))));

    [Test]
    public async Task UfwAddsMissingRulesSetsDefaultAndEnables()
    {
        var host = NewHost().On("ufw show added", "status inactive\ndefault \"ACCEPT\"\nrule allow 22/tcp\n");
        var task = Task("ufw", null, Map(("default", Text("deny")), ("allow", new ListValue([Map(("tcp", Number(22))), Map(("tcp", Number(443)))]))));

        var (probe, changes) = await Probe(new UfwTask(), host, task);
        var script = await Apply(new UfwTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "~ default incoming allow -> deny", "+ allow 443/tcp", "~ enable firewall" }));
        Assert.That(script, Does.Contain("ufw default 'deny' incoming").And.Contain("ufw 'allow' '443/tcp'").And.Contain("ufw --force enable")
            .And.Contain("refusing to enable ufw: no rule allows SSH port 22"));
    }

    [Test]
    public async Task UfwActiveWithAllRulesHasNoChanges()
    {
        var host = NewHost().On("ufw show added", "status active\ndefault \"DROP\"\nrule allow 22/tcp\n");

        var (probe, _) = await Probe(new UfwTask(), host, Task("ufw", null, Map(("default", Text("deny")), ("allow", new ListValue([Map(("tcp", Number(22)))])))));

        Assert.That(probe.Changes, Is.Empty);
    }

    [Test]
    public async Task ServiceIsEnabledAndStartedWhenNeeded()
    {
        var host = NewHost().On("systemctl is-enabled", "enabled disabled\nactive inactive\n");
        var task = Task("service", "docker", Map(("enabled", Flag(true)), ("state", Text("running"))));

        var (probe, changes) = await Probe(new ServiceTask(), host, task);
        var script = await Apply(new ServiceTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "~ enable", "~ start" }));
        Assert.That(script, Does.Contain("systemctl enable 'docker'").And.Contain("systemctl start 'docker'"));
    }

    [Test]
    public async Task ServiceStopAndDisableAreRemovals()
    {
        var host = NewHost().On("systemctl is-enabled", "enabled enabled\nactive active\n");

        var (_, changes) = await Probe(new ServiceTask(), host, Task("service", "cron", Map(("enabled", Flag(false)), ("state", Text("stopped")))));

        Assert.That(changes, Is.EqualTo(new[] { "- disable", "- stop" }));
    }

    [Test]
    public async Task ServiceInDesiredStateHasNoChangesButRestartAlwaysRuns()
    {
        var host = NewHost().On("systemctl is-enabled", "enabled enabled\nactive active\n");

        var (steady, _) = await Probe(new ServiceTask(), host, Task("service", "cron", Map(("enabled", Flag(true)), ("state", Text("started")))));
        var (restart, changes) = await Probe(new ServiceTask(), host, Task("service", "cron", Map(("state", Text("restarted")))));

        Assert.That(steady.Changes, Is.Empty);
        Assert.That(changes, Is.EqualTo(new[] { "! restart" }));
        Assert.That(restart.HasDrift, Is.False);
        Assert.That(restart.NeedsApply, Is.True);
    }

    [Test]
    public async Task ShellAlwaysRunsAndIsNotDrift()
    {
        var host = NewHost();
        var task = Task("shell", null, Map(("command", Text("echo hi"))));

        var (probe, changes) = await Probe(new ShellTask(), host, task);
        var script = await Apply(new ShellTask(), host, task, probe);

        Assert.That(changes, Is.EqualTo(new[] { "! echo hi" }));
        Assert.That(probe.HasDrift, Is.False);
        Assert.That(script, Is.EqualTo("echo hi"));
    }

    [Test]
    public void FailedRemoteCommandsBecomeHostErrors()
    {
        var host = NewHost().On("dpkg-query", "", exitCode: 100, stderr: "E: broken");

        var error = Assert.ThrowsAsync<HostException>(() => new AptTask().ProbeAsync(Context(host, Task("apt", null, Map(("packages", List("x"))))), CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("exit code 100").And.Contain("E: broken"));
    }
}