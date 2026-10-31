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

public sealed record ServiceStatus(string Name, string State, string Health)
{
    public bool Ready => State == "running" && Health is "healthy" or "none";

    public bool Stopped => State is "exited" or "dead" or "missing";

    public string Describe() => State == "missing" ? "no container" : Health == "none" ? State : $"{State}, {Health}";
}

public static class ComposeHealth
{
    const int LogLines = 20;

    const string TcpGet =
        "exec 3<>\"/dev/tcp/$1/$2\" && printf 'GET %s HTTP/1.0\\r\\nHost: %s\\r\\nConnection: close\\r\\n\\r\\n' \"$3\" \"$1\" >&3"
        + " && read -r -t 5 status <&3 && case \"$status\" in \"HTTP/\"*\" \"[23]??*) exit 0;; esac; exit 1";

    public static string HttpTest(string url)
    {
        var quoted = Sh.Quote(url);
        var fallback = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
            ? $"bash -c {Sh.Quote(TcpGet)} healthcheck {Sh.Join([uri.IdnHost, uri.Port.ToString(CultureInfo.InvariantCulture), uri.PathAndQuery])}"
            : "exit 1";
        return $"if command -v curl >/dev/null 2>&1; then curl -fsS -o /dev/null --max-time 5 {quoted}; "
               + $"elif command -v wget >/dev/null 2>&1; then wget -q -O /dev/null -T 5 {quoted}; "
               + $"else {fallback}; fi";
    }

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
            .AppendLine("  echo \"$s $(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \"$id\")\"")
            .AppendLine("done")
            .ToString();
        var result = await connection.RunCheckedAsync($"compose {files.Project} status", script, cancellationToken);
        var lines = Sh.Lines(result.Stdout).ToDictionary(line => line.Key, line => line.Value, StringComparer.Ordinal);

        return
        [
            .. files.Services.Select(service =>
            {
                var parts = lines.GetValueOrDefault(service.Name, "missing").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return new ServiceStatus(service.Name, parts.ElementAtOrDefault(0) ?? "missing", parts.ElementAtOrDefault(1) ?? "none");
            })
        ];
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