using System.Text;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Hosts;

public class HostSessionTests
{
    const string Noble = "ID=ubuntu\nVERSION_ID=\"24.04\"\nVERSION_CODENAME=noble\nINFRA_SSH_PORT=2222\n";

    sealed class FakeShell(Func<string, string, CommandResult> respond) : IRemoteShell
    {
        public List<(string Command, string Stdin)> Calls { get; } = [];

        public Task<CommandResult> ExecAsync(string command, ReadOnlyMemory<byte> stdin, CancellationToken cancellationToken)
        {
            var input = Encoding.UTF8.GetString(stdin.Span);
            Calls.Add((command, input));
            return Task.FromResult(respond(command, input));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static CommandResult Ok(string stdout = "") => new(0, stdout, "");

    static CommandResult Fail() => new(1, "", "sudo: a password is required");

    static FakeShell Shell(string osRelease = Noble, bool passwordless = false, string password = "hunter2") => new((command, stdin) => command switch
    {
        HostFacts.ProbeCommand => Ok(osRelease),
        "sudo -n true" => passwordless ? Ok() : Fail(),
        "sudo -k -S -p '' true" => stdin == password + "\n" ? Ok() : Fail(),
        _ => Ok("ran"),
    });

    static HostConnectOptions Connect(BecomePasswordProvider? provider = null, InfraLog? log = null) =>
        new() { Address = "10.0.0.1", BecomePassword = provider, Log = log ?? InfraLog.Silent() };

    [Test]
    public async Task UbuntuFactsAreRead()
    {
        await using var session = await HostSession.OpenAsync(Shell(), FakeHost.Node(), Connect());

        Assert.That(session.Facts, Is.EqualTo(new HostFacts("ubuntu", "24.04", "noble") { SshPort = 2222 }));
        Assert.That(session.Become, Is.EqualTo(BecomeMode.None));
    }

    [TestCase("ID=debian\nVERSION_ID=\"12\"\n", "debian 12")]
    [TestCase("ID=ubuntu\nVERSION_ID=\"20.04\"\n", "ubuntu 20.04")]
    public void OtherSystemsAreAClearPerHostError(string osRelease, string shown)
    {
        var error = Assert.ThrowsAsync<HostException>(() => HostSession.OpenAsync(Shell(osRelease), FakeHost.Node("old"), Connect()));

        Assert.That(error!.Message, Does.Contain($"'old' runs {shown}").And.Contain("22.04 and 24.04"));
        Assert.That(error.Host, Is.EqualTo("old"));
    }

    [Test]
    public async Task PasswordlessSudoNeedsNoPassword()
    {
        var asked = false;
        var shell = Shell(passwordless: true);
        await using var session = await HostSession.OpenAsync(shell, FakeHost.Node(become: true), Connect((_, _) =>
        {
            asked = true;
            return Task.FromResult("x");
        }));

        await session.RunAsync("id -u");

        Assert.That(asked, Is.False);
        Assert.That(session.Become, Is.EqualTo(BecomeMode.Passwordless));
        Assert.That(shell.Calls[^1].Command, Does.StartWith("sudo -n sh -c "));
        Assert.That(shell.Calls[^1].Stdin, Does.EndWith("id -u"));
    }

    [Test]
    public async Task PasswordGoesOverStdinNeverArgvOrLogs()
    {
        var shell = Shell();
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        await using var session = await HostSession.OpenAsync(shell, FakeHost.Node(become: true), Connect((_, _) => Task.FromResult("hunter2"), log));

        await session.RunAsync("echo hunter2 && id -u");

        var (command, stdin) = shell.Calls[^1];
        Assert.That(session.Become, Is.EqualTo(BecomeMode.Password));
        Assert.That(command, Does.StartWith("sudo -k -S -p '' sh -c ").And.Not.Contain("hunter2"));
        Assert.That(stdin, Does.StartWith("hunter2\nexport LC_ALL=C\n"));
        Assert.That(shell.Calls.Select(call => call.Command), Has.None.Contains("hunter2"));
        Assert.That(events.ToString(), Does.Not.Contain("hunter2").And.Contain("host.command"));
    }

    [Test]
    public void WrongPasswordIsRejected()
    {
        var error = Assert.ThrowsAsync<HostException>(() => HostSession.OpenAsync(Shell(), FakeHost.Node(become: true), Connect((_, _) => Task.FromResult("nope"))));

        Assert.That(error!.Message, Does.Contain("rejected the password"));
    }

    [Test]
    public void MissingPasswordNamesTheSecretToSet()
    {
        var error = Assert.ThrowsAsync<HostException>(() => HostSession.OpenAsync(Shell(), FakeHost.Node("build-1", become: true), Connect()));

        Assert.That(error!.Message, Does.Contain("secret \"hosts/build-1/sudo\""));
    }

    [Test]
    public void RunnerReadsExactlyTheScriptSoStdinStaysForTheScript()
    {
        var command = HostSession.Command(BecomeMode.None, 42);

        Assert.That(command, Does.Contain("count=42 iflag=count_bytes,fullblock"));
    }

    [Test]
    public async Task ExtraStdinFollowsTheScript()
    {
        var shell = Shell();
        await using var session = await HostSession.OpenAsync(shell, FakeHost.Node(), Connect());

        await session.RunAsync("cat", Encoding.UTF8.GetBytes("payload"));

        Assert.That(shell.Calls[^1].Stdin, Is.EqualTo("export LC_ALL=C\ncatpayload"));
        Assert.That(shell.Calls[^1].Command, Does.Contain($"count={"export LC_ALL=C\ncat".Length} "));
    }

    [Test]
    public async Task UploadsKeepContentOutOfTheLog()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        await using var session = await HostSession.OpenAsync(Shell(), FakeHost.Node(), Connect(log: log));

        await session.UploadAsync("/etc/app.env", Encoding.UTF8.GetBytes("TOKEN=abc"), "0600", "root");

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("TOKEN=abc"));
        Assert.That(events.ToString(), Does.Contain("upload /etc/app.env (9 bytes, mode 0600, owner root)").And.Not.Contain(base64));
    }

    [Test]
    public async Task CommandsAreProviderTrafficWithOutputAndExtraStdin()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        await using var session = await HostSession.OpenAsync(Shell(), FakeHost.Node("web-1"), Connect(log: log));

        await session.RunAsync("cat", Encoding.UTF8.GetBytes("payload"));

        var lines = TestLogs.Events(events.ToString());
        var request = lines.Last(line => line.EventName() == "provider.request");
        var response = lines.Last(line => line.EventName() == "provider.response");
        Assert.That(request.GetProperty("Provider").GetString(), Is.EqualTo("ssh:web-1"));
        Assert.That(request.GetProperty("Script").GetString(), Is.EqualTo("cat"));
        Assert.That(request.GetProperty("Stdin").GetString(), Is.EqualTo("payload"));
        Assert.That(response.GetProperty("RequestId").GetString(), Is.EqualTo(request.GetProperty("RequestId").GetString()));
        Assert.That(response.TryGetProperty("ExitCode", out _), Is.True);
    }

    [Test]
    public async Task ReadFileKeepsContentOutOfTheLog()
    {
        var events = new StringWriter();
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("TOKEN=abc"));
        using var log = TestLogs.Writing(events);
        var shell = new FakeShell((command, _) => command == HostFacts.ProbeCommand ? Ok(Noble) : Ok(content));
        await using var session = await HostSession.OpenAsync(shell, FakeHost.Node(), Connect(log: log));

        await session.ReadFileAsync("/etc/app.env");

        Assert.That(events.ToString(), Does.Contain("read /etc/app.env").And.Not.Contain(content));
    }

    static BecomePasswords Passwords(bool canPrompt, TestConsole? console = null, Func<string, string?>? variables = null) =>
        new(new EnvironmentSecretResolver(variables ?? (_ => null)), new InfraWorkspace(Path.GetTempPath()), null, console ?? new TestConsole(), canPrompt, InfraLog.Silent());

    [Test]
    public async Task BecomePasswordComesFromTheDeclaredSecret()
    {
        var passwords = Passwords(false, variables: name => name == "INFRA_SECRET_HOSTS_WEB_SUDO" ? "s3cret" : null);

        var value = await passwords.GetAsync(FakeHost.Node(become: true, becomePassword: new SecretRef("hosts/web/sudo")), CancellationToken.None);

        Assert.That(value, Is.EqualTo("s3cret"));
    }

    [Test]
    public void DeclaredButMissingSecretFails()
    {
        var error = Assert.ThrowsAsync<HostException>(() => Passwords(true).GetAsync(FakeHost.Node(become: true, becomePassword: new SecretRef("hosts/web/sudo")), CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("'hosts/web/sudo'").And.Contain("not set"));
    }

    [Test]
    public void WithoutPromptingAMissingPasswordFailsNamingTheSecret()
    {
        var error = Assert.ThrowsAsync<HostException>(() => Passwords(false).GetAsync(FakeHost.Node("web", become: true), CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("hosts/web/sudo").And.Contain("--yes"));
    }

    [Test]
    public async Task InteractivePromptIsAskedOncePerHost()
    {
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("typed-pw");
        var passwords = Passwords(true, console);
        var host = FakeHost.Node(become: true);

        var first = await passwords.GetAsync(host, CancellationToken.None);
        var second = await passwords.GetAsync(host, CancellationToken.None);

        Assert.That(first, Is.EqualTo("typed-pw"));
        Assert.That(second, Is.EqualTo("typed-pw"));
        Assert.That(console.Output, Does.Contain("sudo password for root@web").And.Not.Contain("typed-pw"));
    }

    [Test]
    public void ProxyCommandIsDetectedForMatchingHosts()
    {
        var config = Path.GetTempFileName();
        var included = Path.GetTempFileName();
        File.WriteAllText(included, "Host jump-*\n  ProxyCommand ssh -W %h:%p bastion\n");
        File.WriteAllText(config, $"""
                                   Include {included}
                                   Host *.internal !skip.internal
                                     ProxyCommand nc %h %p
                                   Host plain
                                     ProxyCommand none
                                   """);
        try
        {
            Assert.That(SshConfigFile.ProxyCommand([config], "db.internal"), Is.EqualTo("nc %h %p"));
            Assert.That(SshConfigFile.ProxyCommand([config], "skip.internal"), Is.Null);
            Assert.That(SshConfigFile.ProxyCommand([config], "plain"), Is.Null);
            Assert.That(SshConfigFile.ProxyCommand([config], "jump-1"), Is.EqualTo("ssh -W %h:%p bastion"));
            Assert.That(SshConfigFile.ProxyCommand([config], "10.0.0.1"), Is.Null);
        }
        finally
        {
            File.Delete(config);
            File.Delete(included);
        }
    }

    [Test]
    public void SshConnectorRefusesProxyCommandBeforeConnecting()
    {
        var config = Path.GetTempFileName();
        File.WriteAllText(config, "Host 10.0.0.1\n  ProxyCommand nc %h %p\n");
        try
        {
            var connector = new SshHostConnector(new SshConnectorOptions { ConfigFiles = [config] });

            var error = Assert.ThrowsAsync<HostException>(() => connector.ConnectAsync(FakeHost.Node(), Connect()));

            Assert.That(error!.Message, Does.Contain("ProxyCommand is not supported"));
        }
        finally
        {
            File.Delete(config);
        }
    }
}