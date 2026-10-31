using System.Text;

namespace RepoInfraCli.Features.Infra.Compose;

public static class ComposeEnvFile
{
    public const string Header = "# Written by infra apply. Holds the secret values compose.yaml references as ${NAME}.\n";

    public static string Write(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var builder = new StringBuilder(Header);
        foreach (var (name, value) in variables)
            builder.Append(name).Append('=').Append(Quote(value)).Append('\n');
        return builder.ToString();
    }

    static string Quote(string value)
    {
        if (!value.Contains('\'') && !value.Contains('\n') && !value.Contains('\r'))
            return $"'{value}'";

        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '$' => "\\$",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => character.ToString(),
            });
        }
        return builder.Append('"').ToString();
    }

    public static IReadOnlyDictionary<string, string> Lines(string content) =>
        content.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => (Name: line[..line.IndexOf('=')], Line: line))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Line, StringComparer.Ordinal);
}
