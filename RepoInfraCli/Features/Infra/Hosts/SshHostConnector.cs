using System.Net;
using RepoInfraCli.Features.Infra.Graph;
using Tmds.Ssh;

namespace RepoInfraCli.Features.Infra.Hosts;

public sealed record SshConnectorOptions
{
    public IReadOnlyList<string>? ConfigFiles { get; init; }

    public IReadOnlyList<string>? KnownHostsFiles { get; init; }

    public IReadOnlyList<string>? IdentityFiles { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class SshHostConnector(SshConnectorOptions? options = null) : IHostConnector
{
    readonly SshConnectorOptions settings = options ?? new SshConnectorOptions();

    public async Task<IHostConnection> ConnectAsync(HostNode host, HostConnectOptions connect, CancellationToken cancellationToken = default)
    {
        var shell = await SshRemoteShell.ConnectAsync(host, connect, settings, cancellationToken);
        try
        {
            return await HostSession.OpenAsync(shell, host, connect, cancellationToken);
        }
        catch
        {
            await shell.DisposeAsync();
            throw;
        }
    }
}

sealed class SshRemoteShell(SshClient client) : IRemoteShell
{
    public static async Task<SshRemoteShell> ConnectAsync(HostNode host, HostConnectOptions connect, SshConnectorOptions settings, CancellationToken cancellationToken)
    {
        var configFiles = settings.ConfigFiles ?? SshConfigSettings.DefaultConfigFilePaths;
        if (SshConfigFile.ProxyCommand(configFiles, connect.Address) is not null)
            throw new HostException(host.Name, $"ssh config sets ProxyCommand for {connect.Address}; ProxyCommand is not supported, use ProxyJump instead");

        var destination = Destination(host, connect.Address);
        var verdict = new HostKeyVerdict();
        var config = new SshConfigSettings
        {
            ConfigFilePaths = [.. configFiles],
            AutoConnect = false,
            AutoReconnect = false,
            ConnectTimeout = settings.ConnectTimeout,
            PostConfigure = context => Configure(context.Settings, connect, settings, verdict),
        };

        var client = new SshClient(destination, config);
        try
        {
            await client.ConnectAsync(cancellationToken);
            connect.Log.Debug("host.connect", $"Connected to {destination}", new Dictionary<string, object?> { ["host"] = host.Name, ["destination"] = destination });
            return new SshRemoteShell(client);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            client.Dispose();
            throw new HostException(host.Name, verdict.Result switch
            {
                KnownHostResult.Unknown => $"host key of {destination} is not in known_hosts; verify it and rerun with --accept-new-hosts to trust it",
                KnownHostResult.Changed => $"host key of {destination} CHANGED since it was trusted; refusing to connect. If the change is expected, remove the old key with: ssh-keygen -R {connect.Address}",
                KnownHostResult.Revoked => $"host key of {destination} is revoked in known_hosts",
                _ => $"cannot connect to {destination}: {exception.Message}",
            });
        }
    }

    static void Configure(SshClientSettings client, HostConnectOptions connect, SshConnectorOptions settings, HostKeyVerdict verdict)
    {
        client.BatchMode = true;
        if (settings.KnownHostsFiles is { } knownHosts)
        {
            client.UserKnownHostsFilePaths = [.. knownHosts];
            client.GlobalKnownHostsFilePaths = [];
        }
        if (settings.IdentityFiles is { } identities)
            client.Credentials = [.. identities.Select(path => new PrivateKeyCredential(path))];

        var trustNew = connect.AcceptNewHosts || connect.TrustOnFirstUse;
        client.UpdateKnownHostsFileAfterAuthentication = trustNew;
        client.HostAuthentication = (context, _) =>
        {
            verdict.Result = context.KnownHostResult;
            return ValueTask.FromResult(context.KnownHostResult == KnownHostResult.Unknown && trustNew);
        };
    }

    static string Destination(HostNode host, string address)
    {
        var target = address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;
        var user = host.User.Length > 0 ? host.User + "@" : "";
        return host.Port is { } port ? $"{user}{target}:{port}" : $"{user}{target}";
    }

    public async Task<CommandResult> ExecAsync(string command, ReadOnlyMemory<byte> stdin, CancellationToken cancellationToken)
    {
        using var process = await client.ExecuteAsync(command, cancellationToken);
        if (!stdin.IsEmpty)
            await process.WriteAsync(stdin, cancellationToken);
        process.WriteEof();
        var (stdout, stderr) = await process.ReadToEndAsStringAsync(cancellationToken);
        var exitCode = await process.GetExitCodeAsync(cancellationToken);
        return new CommandResult(exitCode, stdout, stderr);
    }

    public async Task<IPortForward> ForwardAsync(string remoteHost, int remotePort, CancellationToken cancellationToken)
    {
        var forward = await client.StartForwardAsync(new IPEndPoint(IPAddress.Loopback, 0), new RemoteHostEndPoint(remoteHost, remotePort), cancellationToken);
        return new SshPortForward(forward);
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    sealed class SshPortForward(LocalForward forward) : IPortForward
    {
        public int LocalPort => ((IPEndPoint)forward.ListenEndPoint!).Port;

        public void Dispose() => forward.Dispose();
    }

    sealed class HostKeyVerdict
    {
        public KnownHostResult? Result { get; set; }
    }
}
