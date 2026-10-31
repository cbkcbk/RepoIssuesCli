using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class ServiceTask : IHostTask
{
    static readonly HashSet<string> EnabledStates = ["enabled", "enabled-runtime", "static", "alias", "indirect", "generated"];
    static readonly HashSet<string> ActiveStates = ["active", "activating", "reloading"];

    public string Kind => TaskKinds.Service;

    public string Label(TaskNode task) => $"service {task.Name}";

    sealed record State(IReadOnlyList<string> Actions);

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var name = Sh.Quote(context.Name);
        var script = $"""
            echo "enabled $(systemctl is-enabled {name} 2>/dev/null || true)"
            echo "active $(systemctl is-active {name} 2>/dev/null || true)"
            """;
        var result = await context.Connection.RunCheckedAsync($"service {context.Name} probe", script, cancellationToken);
        var facts = Sh.Lines(result.Stdout).ToDictionary(line => line.Key, line => line.Value.Trim());
        var enabled = EnabledStates.Contains(facts.GetValueOrDefault("enabled", ""));
        var active = ActiveStates.Contains(facts.GetValueOrDefault("active", ""));

        var actions = new List<string>();
        var changes = new List<Change>();
        switch (context.Options.Flag("enabled"))
        {
            case true when !enabled:
                actions.Add("enable");
                changes.Add(Change.Update("enable"));
                break;
            case false when enabled:
                actions.Add("disable");
                changes.Add(Change.Remove("disable"));
                break;
        }

        switch (context.Options.GetString("state"))
        {
            case "started" or "running" when !active:
                actions.Add("start");
                changes.Add(Change.Update("start"));
                break;
            case "stopped" when active:
                actions.Add("stop");
                changes.Add(Change.Remove("stop"));
                break;
            case "restarted":
                actions.Add("restart");
                changes.Add(Change.Run("restart"));
                break;
            case "reloaded":
                actions.Add("reload");
                changes.Add(Change.Run("reload"));
                break;
        }
        return new TaskProbe(changes, new State(actions));
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var state = (State)probe.State!;
        var script = new StringBuilder().AppendLine("set -eu");
        foreach (var action in state.Actions)
            script.AppendLine($"systemctl {action} {Sh.Quote(context.Name)}");
        await context.Connection.RunCheckedAsync($"service {context.Name}", script.ToString(), cancellationToken);
    }

    public static string Verb(string state) => state switch
    {
        "started" or "running" => "start",
        "stopped" => "stop",
        "reloaded" => "reload",
        _ => "restart",
    };
}
