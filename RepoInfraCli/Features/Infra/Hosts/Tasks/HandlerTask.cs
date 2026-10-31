using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class HandlerTask : IHostTask
{
    public string Kind => TaskKinds.Handler;

    public string Label(TaskNode task) => $"handler {task.Options.GetString("service")}";

    public static Change Notified(TaskNode handler) => Change.Update($"{handler.Options.GetString("state")} (notified)");

    public Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken) => Task.FromResult(TaskProbe.None);

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var service = context.Options.GetString("service") ?? "";
        var verb = ServiceTask.Verb(context.Options.GetString("state") ?? "restarted");
        await context.Connection.RunCheckedAsync($"handler {service}", $"systemctl {verb} {Sh.Quote(service)}", cancellationToken);
    }
}
