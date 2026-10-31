using System.Text;
using RepoInfraCli.Features.Infra.Compose;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Hosts.Tasks;

public sealed class ComposeTask(ComposeWait? wait = null) : IHostTask
{
    const string YamlFile = "compose.yaml";
    const string EnvFile = ".env";
    const string EnvMode = "600";

    readonly ComposeWait healthWait = wait ?? ComposeWait.Default;

    public string Kind => TaskKinds.Compose;

    public string Label(TaskNode task) => $"compose {task.Name}";

    sealed record HostFile(string Hash, string Mode);

    sealed record RunningService(string Image, string State);

    sealed record ComposeFacts(IReadOnlyDictionary<string, HostFile> Files, bool Docker, IReadOnlySet<string> Networks, IReadOnlyDictionary<string, RunningService> Services);

    public static ComposeFiles Render(TaskContext context, bool strict) =>
        ComposeDocument.Render(context.Name, context.Options, context.Secrets.Get, context.Live, strict);

    public async Task<TaskProbe> ProbeAsync(TaskContext context, CancellationToken cancellationToken)
    {
        var files = Render(context, strict: false);
        var facts = await FactsAsync(context.Connection, files, cancellationToken);
        var changes = new List<Change>();

        if (!facts.Files.TryGetValue(YamlFile, out var yaml))
        {
            changes.Add(Change.Add($"create {files.YamlPath} and {EnvFile} ({Describe(files)})"));
        }
        else
        {
            var yamlChanged = yaml.Hash != Hash(files.Yaml);
            if (yamlChanged)
                changes.AddRange(await YamlChangesAsync(context.Connection, files, yaml, cancellationToken));
            changes.AddRange(await EnvChangesAsync(context.Connection, files, facts, cancellationToken));
            if (!yamlChanged && facts.Docker)
                changes.AddRange(RunningChanges(files, facts));
        }

        changes.AddRange(files.Networks.Where(network => !facts.Networks.Contains(network)).Select(network => Change.Add($"network {network} (external)")));
        if (files.Unknown.Count > 0 && changes.Count > 0)
            changes[0] = changes[0] with { Text = $"{changes[0].Text}; {InfraValue.UnknownDisplay}: {string.Join(", ", files.Unknown)}" };
        return new TaskProbe(changes, files);
    }

    public async Task ApplyAsync(TaskContext context, TaskProbe probe, CancellationToken cancellationToken)
    {
        var files = Render(context, strict: true);
        var connection = context.Connection;

        await connection.UploadAsync(files.YamlPath, Encoding.UTF8.GetBytes(files.Yaml), "0644", "root:root", cancellationToken);
        await connection.UploadAsync(files.EnvPath, Encoding.UTF8.GetBytes(files.Env), "0" + EnvMode, "root:root", cancellationToken);

        var script = new StringBuilder().AppendLine("set -eu");
        foreach (var network in files.Networks)
            script.AppendLine($"docker network inspect {Sh.Quote(network)} >/dev/null 2>&1 || docker network create {Sh.Quote(network)} >/dev/null");
        script.AppendLine($"cd {Sh.Quote(files.Directory)}")
            .AppendLine($"docker compose -p {Sh.Quote(files.Project)} up -d --remove-orphans 2>&1");

        var result = await connection.RunAsync(script.ToString(), default, cancellationToken);
        if (!result.Succeeded)
            throw new HostException(connection.Host.Name,
                $"docker compose up for {files.Project} failed with exit code {result.ExitCode}: {ComposeHealth.Redact(Tail(result.Stdout + result.Stderr), files.SecretValues)}");

        await ComposeHealth.WaitAsync(connection, files, healthWait, cancellationToken);
    }

    static async Task<ComposeFacts> FactsAsync(IHostConnection connection, ComposeFiles files, CancellationToken cancellationToken)
    {
        var script = new StringBuilder()
            .AppendLine($"d={Sh.Quote(files.Directory)}")
            .AppendLine($"for f in {YamlFile} {EnvFile}; do")
            .AppendLine("  if [ -f \"$d/$f\" ]; then echo \"file $f $(sha256sum \"$d/$f\" | cut -d' ' -f1) $(stat -c '%a' \"$d/$f\")\"; fi")
            .AppendLine("done")
            .AppendLine("if command -v docker >/dev/null 2>&1; then")
            .AppendLine("  echo docker");
        if (files.Networks.Count > 0)
            script.AppendLine($"  for n in {Sh.Join(files.Networks)}; do docker network inspect \"$n\" >/dev/null 2>&1 && echo \"network $n\"; done");
        script.AppendLine($"  docker compose -p {Sh.Quote(files.Project)} ps -a --format '{{{{.Service}}}} {{{{.Image}}}} {{{{.State}}}}' 2>/dev/null | sed 's/^/service /'")
            .AppendLine("fi");

        var result = await connection.RunCheckedAsync($"compose {files.Project} probe", script.ToString(), cancellationToken);
        var hostFiles = new Dictionary<string, HostFile>(StringComparer.Ordinal);
        var networks = new HashSet<string>(StringComparer.Ordinal);
        var services = new Dictionary<string, RunningService>(StringComparer.Ordinal);
        var docker = false;
        foreach (var (key, value) in Sh.Lines(result.Stdout))
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (key)
            {
                case "file" when parts.Length >= 3:
                    hostFiles[parts[0]] = new HostFile(parts[1], parts[2]);
                    break;
                case "docker":
                    docker = true;
                    break;
                case "network" when parts.Length >= 1:
                    networks.Add(parts[0]);
                    break;
                case "service" when parts.Length >= 3:
                    services[parts[0]] = new RunningService(parts[1], parts[2]);
                    break;
            }
        }
        return new ComposeFacts(hostFiles, docker, networks, services);
    }

    static async Task<IEnumerable<Change>> YamlChangesAsync(IHostConnection connection, ComposeFiles files, HostFile current, CancellationToken cancellationToken)
    {
        var content = await connection.ReadFileAsync(files.YamlPath, cancellationToken);
        if (content is not null && ComposeSummary.Yaml(Encoding.UTF8.GetString(content), files.Yaml) is { } summary)
            return summary.Count > 0 ? summary : [Change.Update($"{YamlFile} rewritten")];
        return [Change.Update($"{YamlFile} (sha256 {Short(current.Hash)} -> {Short(Hash(files.Yaml))})")];
    }

    static async Task<IEnumerable<Change>> EnvChangesAsync(IHostConnection connection, ComposeFiles files, ComposeFacts facts, CancellationToken cancellationToken)
    {
        if (!facts.Files.TryGetValue(EnvFile, out var env))
            return [Change.Add($"{EnvFile} ({Secrets(files)})")];

        var changes = new List<Change>();
        if (env.Hash != Hash(files.Env))
        {
            var content = await connection.ReadFileAsync(files.EnvPath, cancellationToken);
            var names = content is null ? "values changed" : ComposeSummary.Env(Encoding.UTF8.GetString(content), files.Env);
            changes.Add(Change.Update($"{EnvFile}: {names} {InfraValue.SecretDisplay}"));
        }
        if (env.Mode != EnvMode)
            changes.Add(Change.Update($"{EnvFile} mode {env.Mode} -> 0{EnvMode}"));
        return changes;
    }

    static IEnumerable<Change> RunningChanges(ComposeFiles files, ComposeFacts facts)
    {
        foreach (var service in files.Services)
        {
            if (!facts.Services.TryGetValue(service.Name, out var running))
                yield return Change.Update($"service {service.Name}: not running");
            else if (service.Image is { } image && running.Image != image)
                yield return Change.Update($"service {service.Name}: running {running.Image} -> {image}");
            else if (running.State != "running")
                yield return Change.Update($"service {service.Name}: {running.State} -> running");
        }
    }

    static string Describe(ComposeFiles files)
    {
        var parts = new List<string> { $"service(s) {string.Join(", ", files.Services.Select(service => service.Name))}" };
        if (files.Networks.Count > 0)
            parts.Add($"network(s) {string.Join(", ", files.Networks)}");
        parts.Add(Secrets(files));
        return string.Join("; ", parts);
    }

    static string Secrets(ComposeFiles files)
    {
        var count = ComposeEnvFile.Lines(files.Env).Count;
        return count == 0 ? "no secrets" : $"{count} secret(s) {InfraValue.SecretDisplay}";
    }

    static string Hash(string text) => FileTask.Sha256(Encoding.UTF8.GetBytes(text));

    static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 1200 ? trimmed : "..." + trimmed[^1200..];
    }
}
