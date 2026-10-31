using System.Security.Cryptography;
using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class AptRepoTask : IHostTask
{
    public const string KeyringFolder = "/etc/apt/keyrings";
    public const string SourcesFolder = "/etc/apt/sources.list.d";
    public static readonly IReadOnlyList<string> DefaultComponents = ["stable"];

    public string Kind => TaskKinds.AptRepo;

    public string Label(TaskNode task) => $"apt_repo {task.Name}";

    sealed record State(string? Keyring);

    public static string SourcesPath(string name) => $"{SourcesFolder}/{name}.sources";

    public static string Sources(TaskContext context, string keyring) =>
        string.Join("", SourceLines(context, keyring).Select(line => line + "\n"));

    static IEnumerable<string> SourceLines(TaskContext context, string keyring) =>
    [
        "Types: deb",
        $"URIs: {context.Options.GetString("url")}",
        $"Suites: {context.Connection.Facts.Codename}",
        $"Components: {string.Join(' ', context.Options.Strings("components") is { Count: > 0 } listed ? listed : DefaultComponents)}",
        $"Signed-By: {keyring}",
    ];

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var name = context.Name;
        var sources = SourcesPath(name);
        var script = $"""
            for k in {KeyringFolder}/{name}.asc {KeyringFolder}/{name}.gpg; do if [ -f "$k" ]; then echo "keyring $k"; break; fi; done
            if [ -f {Sh.Quote(sources)} ]; then echo "sources $(sha256sum < {Sh.Quote(sources)} | cut -d' ' -f1)"; fi
            """;
        var result = await context.Connection.RunCheckedAsync($"apt_repo {name} probe", script, cancellationToken);
        var facts = Sh.Lines(result.Stdout).ToDictionary(line => line.Key, line => line.Value);
        var keyring = facts.GetValueOrDefault("keyring");
        var desired = Sha256(Sources(context, keyring ?? $"{KeyringFolder}/{name}.asc"));
        var current = facts.GetValueOrDefault("sources");

        var changes = new List<Change>();
        if (keyring is null)
            changes.Add(Change.Add($"keyring {KeyringFolder}/{name}.asc from {context.Options.GetString("key")}"));
        if (current is null)
            changes.Add(Change.Add($"source {sources} ({context.Options.GetString("url")} {context.Connection.Facts.Codename})"));
        else if (keyring is not null && current != desired)
            changes.Add(Change.Update($"source {sources}"));
        return new TaskProbe(changes, new State(keyring));
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var state = (State)probe.State!;
        var name = context.Name;
        var script = new StringBuilder()
            .AppendLine("set -eu")
            .AppendLine("export DEBIAN_FRONTEND=noninteractive")
            .AppendLine($"install -d -m 0755 {KeyringFolder}");
        if (state.Keyring is { } existing)
        {
            script.AppendLine($"k={Sh.Quote(existing)}");
        }
        else
        {
            script.AppendLine("if ! command -v curl >/dev/null; then apt-get -q -y update && apt-get -q -y install curl ca-certificates; fi")
                .AppendLine("t=$(mktemp)")
                .AppendLine("trap 'rm -f \"$t\"' EXIT")
                .AppendLine($"curl -fsSL {Sh.Quote(context.Options.GetString("key") ?? "")} -o \"$t\"")
                .AppendLine($"if grep -q -- '-----BEGIN PGP' \"$t\"; then k={KeyringFolder}/{name}.asc; else k={KeyringFolder}/{name}.gpg; fi")
                .AppendLine("install -m 0644 \"$t\" \"$k\"");
        }

        var lines = SourceLines(context, "KEYRING").Select(line => line == "Signed-By: KEYRING" ? "\"Signed-By: $k\"" : Sh.Quote(line));
        script.AppendLine($"printf '%s\\n' {string.Join(' ', lines)} > {Sh.Quote(SourcesPath(name))}")
            .AppendLine($"chmod 0644 {Sh.Quote(SourcesPath(name))}")
            .AppendLine("apt-get -q -y -o DPkg::Lock::Timeout=300 update");
        await context.Connection.RunCheckedAsync($"apt_repo {name}", script.ToString(), cancellationToken);
    }

    static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
