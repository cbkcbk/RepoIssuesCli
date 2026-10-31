using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Hosts;

public interface IHostConnector
{
    Task<IHostConnection> ConnectAsync(HostNode host, HostConnectOptions options, CancellationToken cancellationToken = default);
}

public delegate Task<string> BecomePasswordProvider(HostNode host, CancellationToken cancellationToken);

public sealed record HostConnectOptions
{
    public required string Address { get; init; }

    public bool AcceptNewHosts { get; init; }

    public bool TrustOnFirstUse { get; init; }

    public InfraLog Log { get; init; } = InfraLog.Silent();

    public BecomePasswordProvider? BecomePassword { get; init; }
}
