using System.Diagnostics;

namespace RepoInfraCli.Features.Infra.Logging;

public sealed class TrafficCall
{
    readonly InfraLog log;
    readonly string provider;
    readonly string summary;
    readonly string requestId = Guid.NewGuid().ToString("N")[..12];
    readonly long started = Stopwatch.GetTimestamp();

    internal TrafficCall(InfraLog log, string provider, string summary, IReadOnlyDictionary<string, object?> fields)
    {
        this.log = log;
        this.provider = provider;
        this.summary = summary;
        log.Record(InfraLogLevel.Debug, "provider.request", $"{provider} {summary}", Fields(fields, withDuration: false));
    }

    public string Body(string text) => TrafficRedaction.Body(text, log);

    public void Response(string outcome, IReadOnlyDictionary<string, object?> fields) =>
        log.Record(InfraLogLevel.Debug, "provider.response", $"{provider} {summary}: {outcome}", Fields(fields, withDuration: true));

    public void Failed(Exception exception) =>
        log.Record(InfraLogLevel.Warning, "provider.failed", $"{provider} {summary} failed: {exception.Message}",
            Fields(new Dictionary<string, object?> { ["Exception"] = exception.ToString() }, withDuration: true));

    Dictionary<string, object?> Fields(IReadOnlyDictionary<string, object?> fields, bool withDuration)
    {
        var all = new Dictionary<string, object?>(fields) { ["Provider"] = provider, ["RequestId"] = requestId };
        if (withDuration)
            all["DurationMs"] = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return all;
    }
}