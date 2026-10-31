using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Plan;

public sealed record PlanFilter(IReadOnlyList<string> Plays, IReadOnlyList<string> Hosts, bool HostsOnly)
{
    public static PlanFilter All { get; } = new([], [], false);

    public bool ResourcesOnly { get; init; }

    public bool IncludesPlay(PlayNode play) => !ResourcesOnly && (Plays.Count == 0 || Plays.Contains(play.Name));

    public bool IncludesHost(HostNode host) => Hosts.Count == 0 || Hosts.Contains(host.Name);

    public void Validate(InfraGraph graph)
    {
        if (HostsOnly && ResourcesOnly)
            throw new UsageException("--hosts-only and --resources-only can't be combined.");
        if (Plays.FirstOrDefault(name => graph.Play(name) is null) is { } play)
            throw new PlanTargetNotFoundException($"No play named '{play}'. Plays: {List(graph.Plays.Select(node => node.Name))}.");
        if (Hosts.FirstOrDefault(name => graph.Hosts.All(host => host.Name != name)) is { } host)
            throw new PlanTargetNotFoundException($"No host named '{host}'. Hosts: {List(graph.Hosts.Select(node => node.Name))}.");
    }

    static string List(IEnumerable<string> names) => string.Join(", ", names) is { Length: > 0 } joined ? joined : "(none)";
}

public sealed class PlanTargetNotFoundException(string message) : NotFoundException(message);
