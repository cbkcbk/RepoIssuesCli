using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Parsing;
using Spectre.Console;

namespace RepoInfraCli.Features.Infra.Logging;

public enum InfraLogLevel
{
    Error,
    Warning,
    Info,
    Debug,
}

public sealed class InfraLog(IAnsiConsole? console, int verbosity, Logger? file, ISecretRedactor? redactor = null) : ISecretRedactor, IDisposable
{
    static readonly AsyncLocal<InfraLog?> current = new();

    readonly Lock gate = new();

    public static InfraLog Silent() => new(null, 0, null);

    public static InfraLog? Current => current.Value;

    public static InfraLog Create(IAnsiConsole console, int verbosity, string? logPath, string command, string? env)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return new InfraLog(console, verbosity, null);

        var full = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var file = RunLogger(command, env)
            .WriteTo.File(new RenderedCompactJsonFormatter(), full, encoding: new UTF8Encoding(false))
            .CreateLogger();
        return new InfraLog(console, verbosity, file);
    }

    public static LoggerConfiguration RunLogger(string command, string? env) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.WithProperty("RunId", Guid.CreateVersion7().ToString())
            .Enrich.WithProperty("Command", command)
            .Enrich.WithProperty("Env", env);

    public int Verbosity => verbosity;

    public bool RecordsTraffic => file is not null;

    public ISecretRedactor Secrets { get; } = redactor ?? new SecretRedactor();

    public void AddSecret(string value) => Secrets.AddSecret(value);

    public string Redact(string text) => Secrets.Redact(text);

    public bool ShowsOnConsole(InfraLogLevel level) => (int)level <= 1 + verbosity;

    public IDisposable UseAsCurrent()
    {
        var previous = current.Value;
        current.Value = this;
        return new CurrentScope(previous);
    }

    public void Error(string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Write(InfraLogLevel.Error, eventName, message, fields);

    public void Warning(string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Write(InfraLogLevel.Warning, eventName, message, fields);

    public void Info(string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Write(InfraLogLevel.Info, eventName, message, fields);

    public void Debug(string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null) =>
        Write(InfraLogLevel.Debug, eventName, message, fields);

    public void Write(InfraLogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (console is not null && ShowsOnConsole(level))
        {
            var markup = ConsoleMarkup(level, Redact(message));
            lock (gate)
                console.MarkupLine(markup);
        }

        Record(level, eventName, message, fields);
    }

    public void Record(InfraLogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (file is null)
            return;

        var properties = new List<LogEventProperty> { new("EventName", new ScalarValue(eventName)) };
        foreach (var (key, value) in fields ?? new Dictionary<string, object?>())
            properties.Add(new LogEventProperty(key, new ScalarValue(FieldValue(value))));
        var template = new MessageTemplate([new TextToken(Redact(message))]);
        file.Write(new LogEvent(DateTimeOffset.UtcNow, SerilogLevel(level), null, template, properties));
    }

    public TrafficCall Request(string provider, string summary, IReadOnlyDictionary<string, object?> fields) => new(this, provider, summary, fields);

    object? FieldValue(object? value) => value switch
    {
        null or bool or int or long or double or float or decimal => value,
        _ => Redact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
    };

    static LogEventLevel SerilogLevel(InfraLogLevel level) => level switch
    {
        InfraLogLevel.Error => LogEventLevel.Error,
        InfraLogLevel.Warning => LogEventLevel.Warning,
        InfraLogLevel.Info => LogEventLevel.Information,
        _ => LogEventLevel.Debug,
    };

    static string ConsoleMarkup(InfraLogLevel level, string text) => level switch
    {
        InfraLogLevel.Error => $"[red]Error:[/] {Markup.Escape(text)}",
        InfraLogLevel.Warning => $"[yellow]Warning:[/] {Markup.Escape(text)}",
        InfraLogLevel.Debug => $"[grey]{Markup.Escape(text)}[/]",
        _ => Markup.Escape(text),
    };

    public void Dispose() => file?.Dispose();

    sealed class CurrentScope(InfraLog? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}