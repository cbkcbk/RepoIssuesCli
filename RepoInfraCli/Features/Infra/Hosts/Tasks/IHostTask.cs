using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public interface IHostTask
{
    string Kind { get; }

    string Label(TaskNode task);

    Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken);

    Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken);
}

public sealed record TaskContext(IHostConnection Connection, TaskNode Task, InfraWorkspace Workspace, ILiveValues Live, SecretLookup Secrets)
{
    public MapValue Options => Task.Options;

    public string Name => Task.Name ?? "";
}

public sealed record TaskProbe(IReadOnlyList<Change> Changes, object? State = null)
{
    public static TaskProbe None { get; } = new([]);

    public bool HasDrift => Changes.Any(change => change.Marker.IsDrift());

    public bool NeedsApply => Changes.Any(change => change.Marker.IsActionable());
}

public class TaskPlanException(string message) : Exception(message);