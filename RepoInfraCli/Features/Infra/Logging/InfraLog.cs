using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace RepoInfraCli.Features.Infra.Logging;

public enum InfraLogLevel
{
    Error,
    Warning,
    Info,
    Debug,
}

public sealed class InfraLog(IAnsiConsole? console, int verbosity, TextWriter? events, ISecretRedactor? redactor = null) : ISecretRedactor, IDisposable
{
    readonly Lock gate = new();

    public static InfraLog Silent() => new(null, 0, null);

    public static InfraLog Create(IAnsiConsole console, int verbosity, string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return new InfraLog(console, verbosity, null);

        var full = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var writer = new StreamWriter(full, append: true, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        return new InfraLog(console, verbosity, writer);
    }

    public int Verbosity => verbosity;

    public ISecretRedactor Secrets { get; } = redactor ?? new SecretRedactor();

    public void AddSecret(string value) => Secrets.AddSecret(value);

    public string Redact(string text) => Secrets.Redact(text);

    public bool ShowsOnConsole(InfraLogLevel level) => (int)level <= 1 + verbosity;

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
        var text = Redact(message);
        lock (gate)
        {
            if (console is not null && ShowsOnConsole(level))
                console.MarkupLine(ConsoleMarkup(level, text));
            events?.WriteLine(EventLine(level, eventName, text, fields));
        }
    }

    public void Record(InfraLogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (events is null)
            return;
        var line = EventLine(level, eventName, Redact(message), fields);
        lock (gate)
            events.WriteLine(line);
    }

    static string ConsoleMarkup(InfraLogLevel level, string text) => level switch
    {
        InfraLogLevel.Error => $"[red]Error:[/] {Markup.Escape(text)}",
        InfraLogLevel.Warning => $"[yellow]Warning:[/] {Markup.Escape(text)}",
        InfraLogLevel.Debug => $"[grey]{Markup.Escape(text)}[/]",
        _ => Markup.Escape(text),
    };

    string EventLine(InfraLogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? fields)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("time", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("level", level.ToString().ToLowerInvariant());
            json.WriteString("event", eventName);
            json.WriteString("message", message);
            if (fields is { Count: > 0 })
            {
                json.WriteStartObject("fields");
                foreach (var (key, value) in fields)
                    WriteField(json, key, value);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    void WriteField(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(key);
                break;
            case bool flag:
                json.WriteBoolean(key, flag);
                break;
            case int or long or double or float or decimal:
                json.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteString(key, Redact(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
                break;
        }
    }

    public void Dispose() => events?.Dispose();
}
