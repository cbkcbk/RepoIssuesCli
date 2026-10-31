using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra.Compose;

public abstract record YamlNode;

public sealed record YamlScalar(string Text, bool Quoted = true) : YamlNode;

public sealed record YamlSequence(IReadOnlyList<YamlNode> Items) : YamlNode;

public sealed record YamlMapping(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode;

public sealed record YamlEntry(IReadOnlyList<string> Path, string Value)
{
    public string Key => string.Join('\u001f', Path);
}

public static partial class ComposeYaml
{
    static readonly string[] ReservedWords = ["true", "false", "yes", "no", "on", "off", "null", "~", "y", "n"];

    public static string Write(YamlMapping document)
    {
        var builder = new StringBuilder();
        WriteMapping(builder, document, 0);
        return builder.ToString();
    }

    static void WriteMapping(StringBuilder builder, YamlMapping mapping, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var (key, value) in mapping.Entries)
        {
            builder.Append(pad).Append(Key(key)).Append(':');
            switch (value)
            {
                case YamlScalar scalar:
                    builder.Append(' ').Append(Scalar(scalar)).Append('\n');
                    break;
                case YamlSequence { Items.Count: 0 }:
                    builder.Append(" []\n");
                    break;
                case YamlMapping { Entries.Count: 0 }:
                    builder.Append(" {}\n");
                    break;
                case YamlSequence sequence:
                    builder.Append('\n');
                    foreach (var item in sequence.Items)
                        builder.Append(pad).Append("  - ").Append(Flow(item)).Append('\n');
                    break;
                case YamlMapping nested:
                    builder.Append('\n');
                    WriteMapping(builder, nested, indent + 2);
                    break;
            }
        }
    }

    static string Flow(YamlNode node) => node switch
    {
        YamlScalar scalar => Scalar(scalar),
        YamlSequence sequence => $"[{string.Join(", ", sequence.Items.Select(Flow))}]",
        YamlMapping mapping => $"{{{string.Join(", ", mapping.Entries.Select(entry => $"{Quote(entry.Key)}: {Flow(entry.Value)}"))}}}",
        _ => "null",
    };

    static string Key(string key) =>
        PlainKey().IsMatch(key) && !ReservedWords.Contains(key.ToLowerInvariant()) ? key : Quote(key);

    static string Scalar(YamlScalar scalar) => scalar.Quoted ? Quote(scalar.Text) : scalar.Text;

    public static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var character in text)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\x{(int)character:x2}",
                _ => character.ToString(),
            });
        }
        return builder.Append('"').ToString();
    }

    public static IReadOnlyList<YamlEntry>? Flatten(string yaml)
    {
        var entries = new List<YamlEntry>();
        var stack = new List<(int Indent, string Segment)>();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            var indent = line.Length - line.TrimStart(' ').Length;
            var rest = line[indent..];
            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);
            var parent = stack.Select(item => item.Segment).ToList();

            if (rest.StartsWith("- ", StringComparison.Ordinal))
            {
                var parentKey = string.Join('\u001f', parent);
                var index = counters.GetValueOrDefault(parentKey);
                counters[parentKey] = index + 1;
                entries.Add(new YamlEntry([.. parent, index.ToString(CultureInfo.InvariantCulture)], Unquote(rest[2..])));
                continue;
            }

            if (SplitKey(rest) is not var (key, value))
                return null;
            if (value.Length == 0)
                stack.Add((indent, key));
            else
                entries.Add(new YamlEntry([.. parent, key], Unquote(value)));
        }
        return entries;
    }

    static (string Key, string Value)? SplitKey(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = ClosingQuote(text);
            if (end < 0 || end + 1 >= text.Length || text[end + 1] != ':')
                return null;
            return (Unquote(text[..(end + 1)]), text[(end + 2)..].Trim());
        }

        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0)
            return (text[..colon], text[(colon + 2)..].Trim());
        return text.EndsWith(':') ? (text[..^1], "") : null;
    }

    static int ClosingQuote(string text)
    {
        for (var index = 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
                index++;
            else if (text[index] == '"')
                return index;
        }
        return -1;
    }

    static string Unquote(string text)
    {
        if (text.Length < 2 || text[0] != '"' || ClosingQuote(text) != text.Length - 1)
            return text;
        var builder = new StringBuilder();
        for (var index = 1; index < text.Length - 1; index++)
        {
            if (text[index] != '\\' || index + 1 >= text.Length - 1)
            {
                builder.Append(text[index]);
                continue;
            }
            var escaped = text[++index];
            switch (escaped)
            {
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'x' when index + 2 < text.Length - 1:
                    builder.Append((char)int.Parse(text.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    index += 2;
                    break;
                default: builder.Append(escaped); break;
            }
        }
        return builder.ToString();
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_./-]*$")]
    private static partial Regex PlainKey();
}
