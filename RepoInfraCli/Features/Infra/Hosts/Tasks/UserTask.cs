using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class UserTask : IHostTask
{
    public string Kind => TaskKinds.User;

    public string Label(TaskNode task) => $"user {task.Name}";

    sealed record State(bool Exists, IReadOnlyList<string> Keys);

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var name = Sh.Quote(context.Name);
        var script = $$"""
            if e=$(getent passwd {{name}}); then
              echo "exists 1"
              echo "shell $(echo "$e" | cut -d: -f7)"
              h=$(echo "$e" | cut -d: -f6)
              echo "groups $(id -nG {{name}})"
              echo "primary $(id -gn {{name}})"
              if [ -f "$h/.ssh/authorized_keys" ]; then sed 's/^/key /' "$h/.ssh/authorized_keys"; fi
            else
              echo "exists 0"
            fi
            """;
        var result = await context.Connection.RunCheckedAsync($"user {context.Name} probe", script, cancellationToken);
        var lines = Sh.Lines(result.Stdout).ToList();
        string? Fact(string key) => lines.FirstOrDefault(line => line.Key == key).Value;

        var shell = context.Options.GetString("shell");
        var groups = context.Options.Strings("groups");
        var keys = DesiredKeys(context);

        if (Fact("exists") != "1")
        {
            var details = new List<string>();
            if (shell is not null)
                details.Add($"shell {shell}");
            if (groups.Count > 0)
                details.Add($"groups {string.Join(", ", groups)}");
            if (keys.Count > 0)
                details.Add($"{keys.Count} authorized key(s)");
            return new TaskProbe([Change.Add(details.Count == 0 ? "create" : $"create ({string.Join("; ", details)})")], new State(false, keys));
        }

        var changes = new List<Change>();
        if (shell is not null && Fact("shell") != shell)
            changes.Add(Change.Update($"shell {Fact("shell")} -> {shell}"));

        var current = (Fact("groups") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var added = groups.Where(group => !current.Contains(group)).ToList();
        var removed = ExactGroups(context)
            ? current.Where(group => group != Fact("primary") && !groups.Contains(group)).Order(StringComparer.Ordinal).ToList()
            : [];
        if (added.Count > 0 || removed.Count > 0)
            changes.Add(Change.Update("groups " + string.Join(" ", added.Select(group => "+" + group).Concat(removed.Select(group => "-" + group)))));

        var present = lines.Where(line => line.Key == "key").Select(line => line.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = keys.Where(key => !present.Contains(key)).ToList();
        if (missing.Count > 0)
            changes.Add(Change.Update($"authorized_keys +{missing.Count} key(s)"));
        return new TaskProbe(changes, new State(true, missing));
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var state = (State)probe.State!;
        var name = Sh.Quote(context.Name);
        var shell = context.Options.GetString("shell");
        var groups = context.Options.Strings("groups");
        var script = new StringBuilder().AppendLine("set -eu");

        script.AppendLine($"if ! getent passwd {name} >/dev/null; then useradd -m{(shell is null ? "" : $" -s {Sh.Quote(shell)}")} {name}; fi");
        if (shell is not null)
            script.AppendLine($"usermod -s {Sh.Quote(shell)} {name}");
        if (groups.Count > 0)
            script.AppendLine($"usermod {(ExactGroups(context) ? "-G" : "-aG")} {Sh.Quote(string.Join(',', groups))} {name}");
        if (state.Keys.Count > 0)
        {
            script.AppendLine($"h=$(getent passwd {name} | cut -d: -f6)")
                .AppendLine($"g=$(id -gn {name})")
                .AppendLine($"install -d -m 0700 -o {name} -g \"$g\" \"$h/.ssh\"")
                .AppendLine("a=\"$h/.ssh/authorized_keys\"")
                .AppendLine("touch \"$a\"");
            foreach (var key in state.Keys)
                script.AppendLine($"grep -qxF {Sh.Quote(key)} \"$a\" || printf '%s\\n' {Sh.Quote(key)} >> \"$a\"");
            script.AppendLine("chmod 0600 \"$a\"")
                .AppendLine($"chown {name}:\"$g\" \"$a\"");
        }
        await context.Connection.RunCheckedAsync($"user {context.Name}", script.ToString(), cancellationToken);
    }

    static bool ExactGroups(TaskContext context) => context.Options.Flag("append") == false;

    static IReadOnlyList<string> DesiredKeys(TaskContext context)
    {
        if (context.Options["authorized_keys"] is not ListValue list)
            return [];

        var keys = new List<string>();
        foreach (var item in list.Items)
        {
            var text = item switch
            {
                FileRef file when File.Exists(file.Resolved) => File.ReadAllText(file.Resolved),
                FileRef file => throw new TaskPlanException($"authorized key file {file.Path} does not exist ({file.Resolved})"),
                _ => TaskOptions.Text(item),
            };
            keys.AddRange(text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')));
        }
        return [.. keys.Distinct(StringComparer.Ordinal)];
    }
}
