using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;

namespace RepoInfraCli.Features.Infra.Plan;

public sealed record InfraPlan(
    IReadOnlyList<ResourceChange> Resources,
    IReadOnlyList<PlayPlan> Plays,
    IReadOnlyList<PlanProblem> Problems,
    bool ShowsResources)
{
    public IReadOnlyList<BuildPlan> Builds { get; init; } = [];

    public IEnumerable<TaskPlan> Tasks => Plays.SelectMany(play => play.Hosts).SelectMany(host => host.Roles).SelectMany(role => role.Tasks);

    public IEnumerable<Change> Changes => Resources.SelectMany(resource => resource.Changes).Concat(Builds.SelectMany(build => build.Changes)).Concat(Tasks.SelectMany(task => task.Changes));

    public bool HasDrift => Changes.Any(change => change.Marker.IsDrift());

    public bool HasActions => Changes.Any(change => change.Marker.IsActionable());

    public PlanSummary Summary => PlanSummary.From(Changes);
}

public sealed record PlanSummary(int Add, int Change, int Remove, int Replace, int Run, int Unsupported)
{
    public static PlanSummary From(IEnumerable<Change> changes)
    {
        var counts = changes.GroupBy(change => change.Marker).ToDictionary(group => group.Key, group => group.Count());
        int Count(ChangeMarker marker) => counts.GetValueOrDefault(marker);
        return new PlanSummary(Count(ChangeMarker.Add), Count(ChangeMarker.Update), Count(ChangeMarker.Remove), Count(ChangeMarker.Replace), Count(ChangeMarker.Run), Count(ChangeMarker.Unsupported));
    }
}

public sealed record ResourceChange(string Provider, string Kind, string Name, IReadOnlyList<Change> Changes);

public sealed record PlayPlan(string Name, IReadOnlyList<HostPlan> Hosts);

public enum HostStatus
{
    Planned,
    AddressUnknown,
    External,
    Failed,
}

public sealed record HostPlan(HostNode Host, string? Address, HostStatus Status, string? Note, IReadOnlyList<RolePlan> Roles)
{
    public IEnumerable<TaskPlan> Tasks => Roles.SelectMany(role => role.Tasks);
}

public sealed record RolePlan(string Name, bool External, IReadOnlyList<TaskPlan> Tasks);

public sealed record TaskPlan(TaskNode Task, string Label, TaskProbe Probe, string? Error = null)
{
    public IReadOnlyList<Change> Changes => Probe.Changes;
}

public sealed record PlanProblem(string? Host, string Message);
