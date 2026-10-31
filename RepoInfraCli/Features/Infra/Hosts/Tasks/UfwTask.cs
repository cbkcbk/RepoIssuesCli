using System.Globalization;
using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class UfwTask : IHostTask
{
    public string Kind => TaskKinds.Ufw;

    public string Label(TaskNode task) => "ufw";

    sealed record State(bool SetDefault, IReadOnlyList<string> Rules, bool Enable);

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var desiredDefault = context.Options.GetString("default");
        var rules = Rules(context.Options);
        const string script = """
            if ! command -v ufw >/dev/null; then echo "missing 1"; exit 0; fi
            echo "status $(ufw status | sed -n 's/^Status: //p')"
            if [ -f /etc/default/ufw ]; then . /etc/default/ufw; echo "default ${DEFAULT_INPUT_POLICY:-}"; fi
            ufw show added | sed -n 's/^ufw /rule /p'
            """;
        var result = await context.Connection.RunCheckedAsync("ufw probe", script, cancellationToken);
        var lines = Sh.Lines(result.Stdout).ToList();
        var missing = lines.Any(line => line.Key == "missing");
        var existing = lines.Where(line => line.Key == "rule").Select(line => line.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var currentDefault = Policy(lines.FirstOrDefault(line => line.Key == "default").Value);
        var active = lines.FirstOrDefault(line => line.Key == "status").Value?.Trim() == "active";

        var state = new State(
            desiredDefault is not null && (missing || currentDefault != desiredDefault),
            [.. rules.Where(rule => missing || !existing.Contains(rule))],
            missing || !active);
        var changes = new List<Change>();
        if (state.SetDefault)
            changes.Add(Change.Update($"default incoming {currentDefault ?? "?"} -> {desiredDefault}"));
        changes.AddRange(state.Rules.Select(rule => Change.Add(rule)));
        if (state.Enable)
            changes.Add(Change.Update("enable firewall"));
        return new TaskProbe(changes, state);
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var state = (State)probe.State!;
        var sshPort = (context.Connection.Facts.SshPort ?? context.Connection.Host.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        var script = new StringBuilder()
            .AppendLine("set -eu")
            .AppendLine("if ! command -v ufw >/dev/null; then echo 'ufw is not installed; install it with h:apt { packages = { \"ufw\" } }' >&2; exit 1; fi");
        if (state.SetDefault && context.Options.GetString("default") is { } policy)
            script.AppendLine($"ufw default {Sh.Quote(policy)} incoming");
        foreach (var rule in state.Rules)
            script.AppendLine($"ufw {string.Join(' ', rule.Split(' ').Select(Sh.Quote))}");
        script.AppendLine("if [ \"$(ufw status | sed -n 's/^Status: //p')\" != active ]; then")
            .AppendLine($"  if ! ufw show added | grep -Eq '^ufw allow ({sshPort}/tcp|{sshPort}|from [^ ]+|.* port {sshPort} proto tcp|.* port {sshPort})$'; then")
            .AppendLine($"    echo 'refusing to enable ufw: no rule allows SSH port {sshPort}' >&2; exit 1")
            .AppendLine("  fi")
            .AppendLine("  ufw --force enable")
            .AppendLine("fi");
        await context.Connection.RunCheckedAsync("ufw", script.ToString(), cancellationToken);
    }

    static string? Policy(string? input) => input?.Trim().Trim('"') switch
    {
        "DROP" => "deny",
        "ACCEPT" => "allow",
        "REJECT" => "reject",
        null or "" => null,
        var other => other.ToLowerInvariant(),
    };

    public static IReadOnlyList<string> Rules(MapValue options)
    {
        if (options["allow"] is not ListValue list)
            return [];

        var rules = new List<string>();
        foreach (var item in list.Items.OfType<MapValue>())
        {
            if (item.GetBool("icmp"))
                throw new TaskPlanException("ufw cannot allow icmp with a rule; remove icmp from the FirewallRule (ufw allows echo requests by default)");

            var ports = new List<(string Port, string Proto)>();
            if (item["tcp"] is { } tcp)
                ports.Add((Port(tcp), "tcp"));
            if (item["udp"] is { } udp)
                ports.Add((Port(udp), "udp"));
            IReadOnlyList<string?> sources = item["from"] is ListValue from ? [.. from.Items.Select(TaskOptions.Text)] : [null];
            IReadOnlyList<string?> targets = item["to"] is ListValue to ? [.. to.Items.Select(TaskOptions.Text)] : [null];

            foreach (var source in sources)
            foreach (var target in targets)
            {
                if (ports.Count == 0)
                {
                    if (source is not null || target is not null)
                        rules.Add(Rule(source, target, null, null));
                    continue;
                }
                rules.AddRange(ports.Select(port => Rule(source, target, port.Port, port.Proto)));
            }
        }
        return [.. rules.Distinct(StringComparer.Ordinal)];
    }

    static string Rule(string? source, string? target, string? port, string? proto)
    {
        if (source is null && target is null)
            return $"allow {port}/{proto}";

        var rule = new StringBuilder("allow");
        if (source is not null)
            rule.Append($" from {source}");
        if (target is not null || port is not null)
            rule.Append($" to {target ?? "any"}");
        if (port is not null)
            rule.Append($" port {port} proto {proto}");
        return rule.ToString();
    }

    static string Port(InfraValue value) => value switch
    {
        NumberValue number => ((long)number.Value).ToString(CultureInfo.InvariantCulture),
        _ => TaskOptions.Text(value),
    };
}
