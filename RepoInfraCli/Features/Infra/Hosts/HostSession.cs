using System.Text;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Hosts;

public enum BecomeMode
{
    None,
    Passwordless,
    Password,
}

public sealed class HostSession : IHostConnection
{
    const int MissingFileExitCode = 3;
    const int LoggedOutputLength = 4000;

    readonly IRemoteShell shell;
    readonly InfraLog log;
    string? password;

    HostSession(IRemoteShell shell, HostNode host, InfraLog log)
    {
        this.shell = shell;
        this.log = log;
        Host = host;
    }

    public HostNode Host { get; }

    public HostFacts Facts { get; private set; } = new("", "", "");

    public BecomeMode Become { get; private set; }

    public static async Task<HostSession> OpenAsync(IRemoteShell shell, HostNode host, HostConnectOptions options, CancellationToken cancellationToken = default)
    {
        var session = new HostSession(shell, host, options.Log);
        var release = await session.ExecAsync(HostFacts.ProbeCommand, default, HostFacts.ProbeCommand, cancellationToken);
        if (!release.Succeeded)
            throw new HostException(host.Name, $"cannot read /etc/os-release on '{host.Name}': {release.Stderr.Trim()}");
        session.Facts = HostFacts.Parse(release.Stdout);
        session.Facts.EnsureSupported(host.Name);

        if (host.Become)
            await session.NegotiateBecomeAsync(options.BecomePassword, cancellationToken);
        return session;
    }

    async Task NegotiateBecomeAsync(BecomePasswordProvider? provider, CancellationToken cancellationToken)
    {
        if ((await ExecAsync("sudo -n true", default, "sudo -n true", cancellationToken)).Succeeded)
        {
            Become = BecomeMode.Passwordless;
            return;
        }

        if (provider is null)
            throw new HostException(Host.Name, BecomePasswords.MissingMessage(Host));

        var candidate = await provider(Host, cancellationToken);
        log.AddSecret(candidate);
        var check = await ExecAsync("sudo -k -S -p '' true", Encoding.UTF8.GetBytes(candidate + "\n"), "sudo -S true (password on stdin)", cancellationToken);
        if (!check.Succeeded)
            throw new HostException(Host.Name, $"sudo on '{Host.Name}' rejected the password for {Host.User}");

        password = candidate;
        Become = BecomeMode.Password;
    }

    public Task<CommandResult> RunAsync(string script, ReadOnlyMemory<byte> stdin = default, CancellationToken cancellationToken = default) =>
        RunAsync(script, stdin, script, cancellationToken);

    public async Task UploadAsync(string path, ReadOnlyMemory<byte> content, string? mode, string? owner, CancellationToken cancellationToken = default)
    {
        var script = new StringBuilder()
            .AppendLine("set -eu")
            .AppendLine($"p={Sh.Quote(path)}")
            .AppendLine("d=$(dirname \"$p\")")
            .AppendLine("mkdir -p \"$d\"")
            .AppendLine("t=$(mktemp \"$d/.infra.XXXXXX\")")
            .AppendLine("trap 'rm -f \"$t\"' EXIT")
            .AppendLine("base64 -d > \"$t\" <<'INFRA_EOF'")
            .AppendLine(Convert.ToBase64String(content.Span, Base64FormattingOptions.InsertLineBreaks).ReplaceLineEndings("\n"))
            .AppendLine("INFRA_EOF")
            .AppendLine(mode is null ? "if [ -e \"$p\" ]; then chmod --reference=\"$p\" \"$t\"; else chmod 0644 \"$t\"; fi" : $"chmod {Sh.Quote(mode)} \"$t\"")
            .AppendLine(owner is null ? "if [ -e \"$p\" ]; then chown --reference=\"$p\" \"$t\"; fi" : $"chown {Sh.Quote(owner)} \"$t\"")
            .AppendLine("mv -f \"$t\" \"$p\"")
            .AppendLine("trap - EXIT")
            .ToString();
        var result = await RunAsync(script, default, $"upload {path} ({content.Length} bytes{(mode is null ? "" : $", mode {mode}")}{(owner is null ? "" : $", owner {owner}")})", cancellationToken);
        if (!result.Succeeded)
            throw new HostException(Host.Name, $"upload of {path} failed: {result.Stderr.Trim()}");
    }

    public async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync($"if [ -f {Sh.Quote(path)} ]; then base64 -w0 {Sh.Quote(path)}; else exit {MissingFileExitCode}; fi", default, $"read {path}", cancellationToken);
        if (result.ExitCode == MissingFileExitCode)
            return null;
        if (!result.Succeeded)
            throw new HostException(Host.Name, $"reading {path} failed: {result.Stderr.Trim()}");
        return Convert.FromBase64String(result.Stdout.Trim());
    }

    async Task<CommandResult> RunAsync(string script, ReadOnlyMemory<byte> stdin, string display, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes("export LC_ALL=C\n" + script.ReplaceLineEndings("\n"));
        var prefix = Become == BecomeMode.Password ? Encoding.UTF8.GetBytes(password + "\n") : [];
        var input = new byte[prefix.Length + body.Length + stdin.Length];
        prefix.CopyTo(input, 0);
        body.CopyTo(input, prefix.Length);
        stdin.Span.CopyTo(input.AsSpan(prefix.Length + body.Length));
        return await ExecAsync(Command(Become, body.Length), input, display, cancellationToken);
    }

    public static string Command(BecomeMode become, int scriptLength)
    {
        var runner = $"f=$(mktemp) && dd bs=65536 count={scriptLength} iflag=count_bytes,fullblock status=none > \"$f\" && sh \"$f\"; r=$?; rm -f \"$f\"; exit $r";
        return become switch
        {
            BecomeMode.Passwordless => $"sudo -n sh -c {Sh.Quote(runner)}",
            BecomeMode.Password => $"sudo -k -S -p '' sh -c {Sh.Quote(runner)}",
            _ => $"sh -c {Sh.Quote(runner)}",
        };
    }

    async Task<CommandResult> ExecAsync(string command, ReadOnlyMemory<byte> stdin, string display, CancellationToken cancellationToken)
    {
        var result = await shell.ExecAsync(command, stdin, cancellationToken);
        log.Debug("host.command", $"{Host.Name}$ {FirstLine(display)} (exit {result.ExitCode})", new Dictionary<string, object?>
        {
            ["host"] = Host.Name,
            ["command"] = display,
            ["become"] = Become.ToString(),
            ["exitCode"] = result.ExitCode,
            ["stdout"] = Clip(result.Stdout),
            ["stderr"] = Clip(result.Stderr),
        });
        return result;
    }

    static string FirstLine(string text)
    {
        var lines = text.Trim().Split('\n');
        return lines.Length == 1 ? lines[0] : $"{lines[0]} (+{lines.Length - 1} line(s))";
    }

    static string Clip(string text) => text.Length <= LoggedOutputLength ? text : text[..LoggedOutputLength] + "...";

    public ValueTask DisposeAsync() => shell.DisposeAsync();
}
