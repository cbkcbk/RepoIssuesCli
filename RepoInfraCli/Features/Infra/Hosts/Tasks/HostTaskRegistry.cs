namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class HostTaskRegistry(IEnumerable<IHostTask> tasks)
{
    readonly Dictionary<string, IHostTask> byKind = tasks.ToDictionary(task => task.Kind, StringComparer.Ordinal);

    public static HostTaskRegistry Default { get; } = new(
    [
        new AptTask(),
        new AptRepoTask(),
        new UserTask(),
        new FileTask(),
        new UfwTask(),
        new ServiceTask(),
        new HandlerTask(),
        new ComposeTask(),
        new ShellTask(),
    ]);

    public IHostTask For(string kind) =>
        byKind.TryGetValue(kind, out var task) ? task : throw new TaskPlanException($"no host task module handles '{kind}'");
}
