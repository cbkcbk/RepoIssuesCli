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
            using var log = new InfraLog(console, verbosity, null);
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
    public void EveryEventIsAJsonLineWithRedactedSecrets()
    {
        var console = new TestConsole().Width(200);
        var events = new StringWriter { NewLine = "\n" };
        using var log = new InfraLog(console, 0, events);
        log.AddSecret("hunter2");

        log.Debug("ssh.connect", "connecting with hunter2", new Dictionary<string, object?> { ["password"] = "hunter2", ["port"] = 22, ["ok"] = true, ["none"] = null });
        log.Error("apply.failed", "token hunter2 rejected");

        var lines = events.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0].GetProperty("level").GetString(), Is.EqualTo("debug"));
        Assert.That(lines[0].GetProperty("event").GetString(), Is.EqualTo("ssh.connect"));
        Assert.That(lines[0].GetProperty("message").GetString(), Is.EqualTo("connecting with (secret)"));
        Assert.That(lines[0].GetProperty("fields").GetProperty("password").GetString(), Is.EqualTo("(secret)"));
        Assert.That(lines[0].GetProperty("fields").GetProperty("port").GetDouble(), Is.EqualTo(22));
        Assert.That(lines[0].GetProperty("fields").GetProperty("ok").GetBoolean(), Is.True);
        Assert.That(lines[0].GetProperty("fields").GetProperty("none").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(console.Output, Is.EqualTo("Error: token (secret) rejected\n"));
        Assert.That(events.ToString(), Does.Not.Contain("hunter2"));
    }

    [Test]
    public void RecordWritesOnlyToTheEventLog()
    {
        var console = new TestConsole();
        var events = new StringWriter();
        using var log = new InfraLog(console, 2, events);

        log.Record(InfraLogLevel.Error, "validate.diagnostic", "x");

        Assert.That(console.Output, Is.Empty);
        Assert.That(events.ToString(), Does.Contain("\"validate.diagnostic\""));
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
