using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Apps;

public static class ProcessTraffic
{
    public const string Provider = "process";

    public static TrafficCall? Start(
        InfraLog? log, string fileName, IReadOnlyList<string> arguments, string? workingDirectory, IEnumerable<string> environmentNames, string? stdin)
    {
        if (log is not { RecordsTraffic: true })
            return null;

        return log.Request(Provider, string.Join(' ', [fileName, .. arguments]), new Dictionary<string, object?>
        {
            ["FileName"] = fileName,
            ["Arguments"] = string.Join(' ', arguments),
            ["WorkingDirectory"] = workingDirectory,
            ["Environment"] = string.Join(", ", environmentNames),
            ["Stdin"] = stdin is null ? null : TrafficRedaction.Body(stdin, log),
        });
    }

    public static void Exited(this TrafficCall? call, int exitCode, string stdout, string stderr) =>
        call?.Response($"exit {exitCode}", new Dictionary<string, object?>
        {
            ["ExitCode"] = exitCode,
            ["Stdout"] = call.Body(stdout),
            ["Stderr"] = call.Body(stderr),
        });
}