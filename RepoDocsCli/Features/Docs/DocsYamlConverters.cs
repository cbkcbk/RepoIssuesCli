using System.Globalization;
using RepoCli.Core.Features.Anchors;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace RepoDocsCli.Features.Docs;

public sealed class TimestampYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return DateTimeOffset.TryParse(scalar.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? DocsTime.Truncate(value)
            : throw new YamlException(scalar.Start, scalar.End, $"'{scalar.Value}' is not a timestamp; use {DocsTime.TimestampFormat}.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        emitter.Emit(new Scalar(((DateTimeOffset)value!).ToUniversalTime().ToString(DocsTime.TimestampFormat, CultureInfo.InvariantCulture)));
}

public sealed class DateYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(DateOnly) || type == typeof(DateOnly?);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();
        return DateOnly.TryParseExact(scalar.Value, DocsTime.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : throw new YamlException(scalar.Start, scalar.End, $"'{scalar.Value}' is not a date; use {DocsTime.DateFormat}.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
        emitter.Emit(new Scalar(((DateOnly)value!).ToString(DocsTime.DateFormat, CultureInfo.InvariantCulture)));
}

public sealed class AnchorYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Anchor);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var start = parser.Current?.Start ?? Mark.Empty;
        var fields = (AnchorFields?)rootDeserializer(typeof(AnchorFields));
        if (fields is not { Path: { } path, Context: { } context, Body: { } body } || fields.Line < 1 || fields.Span < 1)
            throw new YamlException(start, start, "An anchor needs path, line, span, context and body.");

        return new Anchor(path, fields.Line, fields.Span, context, body, fields.Symbol);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var anchor = (Anchor)value!;
        serializer(new AnchorFields
        {
            Path = anchor.Path,
            Line = anchor.Line,
            Span = anchor.Span,
            Symbol = anchor.Symbol,
            Context = anchor.Context,
            Body = anchor.Body,
        }, typeof(AnchorFields));
    }

    sealed class AnchorFields
    {
        public string? Path { get; init; }

        public int Line { get; init; }

        public int Span { get; init; }

        public string? Symbol { get; init; }

        public string? Context { get; init; }

        public string? Body { get; init; }
    }
}
