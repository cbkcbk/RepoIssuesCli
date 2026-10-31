namespace RepoInfraCli.Features.Infra.Hosts;

public interface IRemoteShell : IAsyncDisposable
{
    Task<CommandResult> ExecAsync(string command, ReadOnlyMemory<byte> stdin, CancellationToken cancellationToken);
}
