namespace RepoInfraCli.Features.Infra.Hosts;

public interface IRemoteShell : IAsyncDisposable
{
    Task<CommandResult> ExecAsync(string command, ReadOnlyMemory<byte> stdin, CancellationToken cancellationToken);

    Task<IPortForward> ForwardAsync(string remoteHost, int remotePort, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{GetType().Name} can't forward ports");
}
