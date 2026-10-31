using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class ShellTask : IHostTask
{
    public string Kind => TaskKinds.Shell;

    public string Label(TaskNode task) => "shell";

    public Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new TaskProbe([Change.Run(context.Options.GetString("command") ?? "")]));

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken) =>
        await context.Connection.RunCheckedAsync("shell", context.Options.GetString("command") ?? "", cancellationToken);
}
