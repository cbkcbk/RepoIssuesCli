using System.Text.Json;
using RepoInfraCli.Features.Infra.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Spectre.Console;

namespace RepoInfraCli.Tests;

public static class TestLogs
{
    public static InfraLog Writing(StringWriter events, IAnsiConsole? console = null, int verbosity = 2) =>
        new(console, verbosity, InfraLog.RunLogger("test", null).WriteTo.Sink(new WriterSink(new RenderedCompactJsonFormatter(), events)).CreateLogger());

    public static List<JsonElement> Events(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone())];

    public static string EventName(this JsonElement entry) => entry.GetProperty("EventName").GetString()!;

    sealed class WriterSink(ITextFormatter formatter, TextWriter writer) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => formatter.Format(logEvent, writer);
    }
}