using System.Diagnostics;
using System.Globalization;
using System.Text;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Compose;

public sealed record ComposeWait(TimeSpan Timeout, TimeSpan Interval)
{
    public static ComposeWait Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2));
}

public sealed record ServiceStatus(string Name, string State, string Health, string? Address, bool? HttpOk)
{
    public bool Ready => State == "running" && Health is "healthy" or "none" && HttpOk is null or true;

    public bool Stopped => State is "exited" or "dead" or "missing";

    public string Describe() =>
        State == "missing" ? "no container" : string.Join(", ", new[] { State, Health == "none" ? null : Health, HttpOk == false ? "http check failing" : null }.OfType<string>());
}

public static class ComposeHealth
{
    const int LogLines = 20;

    public static async Task WaitAsync(IHostConnection connection, ComposeFiles files, ComposeWait wait, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var statuses = await StatusAsync(connection, files, cancellationToken);
            if (statuses.FirstOrDefault(status => status.Stopped) is { } stopped)
                throw await FailureAsync(connection, files, stopped, "stopped", cancellationToken);
            if (statuses.All(status => status.Ready))
                return;
            if (Stopwatch.GetElapsedTime(started) >= wait.Timeout)
                throw await FailureAsync(connection, files, statuses.First(status => !status.Ready),
                    $"did not become healthy within {wait.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s", cancellationToken);
            await Task.Delay(wait.Interval, cancellationToken);
        }
    }

    public static async Task<IReadOnlyList<ServiceStatus>> StatusAsync(IHostConnection connection, ComposeFiles files, CancellationToken cancellationToken)
    {
        var script = new StringBuilder()
            .AppendLine($"for s in {Sh.Join(files.Services.Select(service => service.Name))}; do")
            .AppendLine($"  id=$(docker compose -p {Sh.Quote(files.Project)} ps -a -q \"$s\" 2>/dev/null | head -n 1)")
            .AppendLine("  if [ -z \"$id\" ]; then echo \"$s missing\"; continue; fi")
            .AppendLine("  echo \"$s $(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}{{range .NetworkSettings.Networks}} {{.IPAddress}}{{end}}' \"$id\")\"")
            .AppendLine("done")
            .ToString();
        var result = await connection.RunCheckedAsync($"compose {files.Project} status", script, cancellationToken);
        var lines = Sh.Lines(result.Stdout).ToDictionary(line => line.Key, line => line.Value, StringComparer.Ordinal);

        var statuses = new List<ServiceStatus>();
        foreach (var service in files.Services)
        {
            var parts = lines.GetValueOrDefault(service.Name, "missing").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var state = parts.ElementAtOrDefault(0) ?? "missing";
            var health = parts.ElementAtOrDefault(1) ?? "none";
            var address = parts.Skip(2).FirstOrDefault(ip => ip.Length > 0);
            bool? http = null;
            if (service.HttpHealthcheck is { } url && state == "running")
                http = await HttpOkAsync(connection, HostUrl(url, address), cancellationToken);
            statuses.Add(new ServiceStatus(service.Name, state, health, address, http));
        }
        return statuses;
    }

    public static string HostUrl(string url, string? address)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || address is null || uri.Host is not ("localhost" or "127.0.0.1" or "0.0.0.0" or "[::1]"))
            return url;
        return new UriBuilder(uri) { Host = address }.Uri.ToString();
    }

    static async Task<bool> HttpOkAsync(IHostConnection connection, string url, CancellationToken cancellationToken)
    {
        var quoted = Sh.Quote(url);
        var script = $"if command -v curl >/dev/null 2>&1; then curl -fsS -o /dev/null --max-time 5 {quoted}; else wget -q -O /dev/null -T 5 {quoted}; fi && echo ok";
        var result = await connection.RunAsync(script, default, cancellationToken);
        return result.Stdout.Trim() == "ok";
    }

    static async Task<Exception> FailureAsync(IHostConnection connection, ComposeFiles files, ServiceStatus status, string what, CancellationToken cancellationToken)
    {
        var logs = await connection.RunAsync($"docker compose -p {Sh.Quote(files.Project)} logs --no-color --tail {LogLines} {Sh.Quote(status.Name)} 2>&1", default, cancellationToken);
        var tail = Redact(logs.Stdout.Trim(), files.SecretValues);
        var message = $"service {status.Name} {what} ({status.Describe()})";
        return new HostException(connection.Host.Name, tail.Length == 0 ? message : $"{message}. Last log lines:\n{tail}");
    }

    public static string Redact(string text, IEnumerable<string> secrets) =>
        secrets.OrderByDescending(secret => secret.Length).Aggregate(text, (current, secret) => current.Replace(secret, SecretRedactor.Replacement, StringComparison.Ordinal));
}
