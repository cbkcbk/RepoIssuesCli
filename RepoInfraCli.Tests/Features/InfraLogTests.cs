using System.Text.Json;
using RepoInfraCli.Features.Infra.Logging;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Features;

public class InfraLogTests
{
    [Test]
    public void ConsoleShowsErrorsAndWarningsAlwaysInfoAtVerboseAndDebugAtVeryVerbose()
    {
        string Shown(int verbosity)
        {
            var console = new TestConsole().Width(200);
            using var log = TestLogs.Writing(new StringWriter(), console, verbosity);
            log.Error("e", "bad");
            log.Warning("w", "careful");
            log.Info("i", "step");
            log.Debug("d", "detail");
            return console.Output;
        }

        Assert.That(Shown(0), Is.EqualTo("Error: bad\nWarning: careful\n"));
        Assert.That(Shown(1), Is.EqualTo("Error: bad\nWarning: careful\nstep\n"));
        Assert.That(Shown(2), Is.EqualTo("Error: bad\nWarning: careful\nstep\ndetail\n"));
    }

    [Test]
    public void EveryEventIsACompactJsonLineWithRedactedSecrets()
    {
        var console = new TestConsole().Width(200);
        var events = new StringWriter();
        using var log = TestLogs.Writing(events, console, verbosity: 0);
        log.AddSecret("hunter2");

        log.Debug("ssh.connect", "connecting {with} hunter2", new Dictionary<string, object?> { ["password"] = "hunter2", ["port"] = 22, ["ok"] = true, ["none"] = null });
        log.Error("apply.failed", "token hunter2 rejected");

        var lines = TestLogs.Events(events.ToString());
        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0].GetProperty("@l").GetString(), Is.EqualTo("Debug"));
        Assert.That(lines[0].TryGetProperty("@t", out _), Is.True);
        Assert.That(lines[0].EventName(), Is.EqualTo("ssh.connect"));
        Assert.That(lines[0].GetProperty("@m").GetString(), Is.EqualTo("connecting {with} (secret)"));
        Assert.That(lines[0].GetProperty("password").GetString(), Is.EqualTo("(secret)"));
        Assert.That(lines[0].GetProperty("port").GetInt32(), Is.EqualTo(22));
        Assert.That(lines[0].GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(lines[0].GetProperty("none").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(lines[0].GetProperty("Command").GetString(), Is.EqualTo("test"));
        Assert.That(lines[0].GetProperty("RunId").GetString(), Is.EqualTo(lines[1].GetProperty("RunId").GetString()));
        Assert.That(console.Output, Is.EqualTo("Error: token (secret) rejected\n"));
        Assert.That(events.ToString(), Does.Not.Contain("hunter2"));
    }

    [Test]
    public void RecordWritesOnlyToTheEventLog()
    {
        var console = new TestConsole();
        var events = new StringWriter();
        using var log = TestLogs.Writing(events, console);

        log.Record(InfraLogLevel.Error, "validate.diagnostic", "x");

        Assert.That(console.Output, Is.Empty);
        Assert.That(events.ToString(), Does.Contain("\"validate.diagnostic\""));
    }

    [Test]
    public void TrafficIsARequestAndResponsePairWithDuration()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);

        var call = log.Request("digitalocean", "GET /v2/droplets", new Dictionary<string, object?> { ["Method"] = "GET" });
        call.Response("200 OK", new Dictionary<string, object?> { ["Status"] = 200 });

        var lines = TestLogs.Events(events.ToString());
        Assert.That(lines.Select(line => line.EventName()), Is.EqualTo(new[] { "provider.request", "provider.response" }));
        Assert.That(lines.Select(line => line.GetProperty("Provider").GetString()), Is.All.EqualTo("digitalocean"));
        Assert.That(lines[0].GetProperty("RequestId").GetString(), Is.EqualTo(lines[1].GetProperty("RequestId").GetString()));
        Assert.That(lines[0].TryGetProperty("DurationMs", out _), Is.False);
        Assert.That(lines[1].GetProperty("DurationMs").GetInt64(), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void LogWithoutAFileRecordsNoTraffic()
    {
        using var log = new InfraLog(new TestConsole(), 2, null);

        Assert.That(log.RecordsTraffic, Is.False);
    }

    [Test]
    public void LongerSecretsAreRedactedFirst()
    {
        var redactor = new SecretRedactor();
        redactor.AddSecret("abc");
        redactor.AddSecret("abcdef");
        redactor.AddSecret("");

        Assert.That(redactor.Redact("x abcdef y abc"), Is.EqualTo("x (secret) y (secret)"));
    }
}