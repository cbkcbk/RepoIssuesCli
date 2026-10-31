using System.Globalization;
using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class AptTask : IHostTask
{
    const string AptGet = "apt-get -q -y -o DPkg::Lock::Timeout=300";

    public string Kind => TaskKinds.Apt;

    public string Label(TaskNode task) => "apt";

    sealed record State(IReadOnlyList<string> Install, IReadOnlyList<string> Remove, int Upgradable);

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var packages = context.Options.Strings("packages");
        var absent = context.Options.Strings("absent");
        var upgrade = context.Options.GetString("upgrade");

        var script = new StringBuilder();
        foreach (var package in packages.Concat(absent).Distinct())
            script.AppendLine($"echo \"pkg {package} $(dpkg-query -W -f='${{db:Status-Abbrev}}' {Sh.Quote(package)} 2>/dev/null || true)\"");
        if (upgrade is not null)
            script.AppendLine($"echo \"upgradable $(apt-get -s {UpgradeCommand(upgrade)} 2>/dev/null | grep -c '^Inst ' || true)\"");
        if (script.Length == 0)
            return TaskProbe.None;

        var result = await context.Connection.RunCheckedAsync("apt probe", script.ToString(), cancellationToken);
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var upgradable = 0;
        foreach (var (key, value) in Sh.Lines(result.Stdout))
        {
            if (key == "pkg" && value.Split(' ', 2) is [var name, var status] && status.StartsWith("ii", StringComparison.Ordinal))
                installed.Add(name);
            if (key == "upgradable")
                upgradable = int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
        }

        var state = new State(
            [.. packages.Where(package => !installed.Contains(package))],
            [.. absent.Where(installed.Contains)],
            upgradable);
        var changes = new List<Change>();
        if (state.Install.Count > 0)
            changes.Add(Change.Add($"install {string.Join(", ", state.Install)}"));
        if (state.Remove.Count > 0)
            changes.Add(Change.Remove($"remove {string.Join(", ", state.Remove)}"));
        if (state.Upgradable > 0)
            changes.Add(Change.Update($"upgrade {state.Upgradable} package(s) ({upgrade})"));
        return new TaskProbe(changes, state);
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var state = (State)probe.State!;
        var update = context.Options.Flag("update") == true;
        var script = new StringBuilder()
            .AppendLine("set -eu")
            .AppendLine("export DEBIAN_FRONTEND=noninteractive");
        script.AppendLine(update
            ? $"{AptGet} update"
            : $"if ! ls /var/lib/apt/lists 2>/dev/null | grep -q _Packages; then {AptGet} update; fi");
        if (state.Install.Count > 0)
            script.AppendLine($"{AptGet} install {Sh.Join(state.Install)}");
        if (state.Remove.Count > 0)
            script.AppendLine($"{AptGet} remove {Sh.Join(state.Remove)}");
        if (state.Upgradable > 0 && context.Options.GetString("upgrade") is { } upgrade)
            script.AppendLine($"{AptGet} {UpgradeCommand(upgrade)}");
        await context.Connection.RunCheckedAsync("apt", script.ToString(), cancellationToken);
    }

    static string UpgradeCommand(string upgrade) => upgrade == "safe" ? "upgrade" : "dist-upgrade";
}
