using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts;

public interface IHostConnection : IAsyncDisposable
{
    HostNode Host { get; }

    HostFacts Facts { get; }

    Task<CommandResult> RunAsync(string script, ReadOnlyMemory<byte> stdin = default, CancellationToken cancellationToken = default);

    Task UploadAsync(string path, ReadOnlyMemory<byte> content, string? mode, string? owner, CancellationToken cancellationToken = default);

    Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

public static class HostConnectionExtensions
{
    public static async Task<CommandResult> RunCheckedAsync(this IHostConnection connection, string what, string script, CancellationToken cancellationToken = default)
    {
        var result = await connection.RunAsync(script, default, cancellationToken);
        if (!result.Succeeded)
            throw new HostException(connection.Host.Name, $"{what} failed with exit code {result.ExitCode}: {Tail(result.Stderr.Length > 0 ? result.Stderr : result.Stdout)}");
        return result;
    }

    static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 600 ? trimmed : "..." + trimmed[^600..];
    }
}

public class HostException(string host, string message) : Exception(message)
{
    public string Host => host;
}
