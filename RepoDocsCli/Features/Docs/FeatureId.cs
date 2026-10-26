using System.Text;
using System.Text.RegularExpressions;

namespace RepoDocsCli.Features.Docs;

public static partial class FeatureId
{
    public const string RepoFileStem = "repo";
    public const string ConfigFileStem = "config";

    static readonly string[] Reserved = [RepoFileStem, ConfigFileStem];

    public static bool IsValid(string id) => ValidPattern().IsMatch(id) && !Reserved.Contains(id);

    public static string Require(string id) =>
        IsValid(id) ? id : throw new UsageException($"'{id}' is not a valid feature id. Use lowercase letters, digits and single hyphens, and not '{RepoFileStem}' or '{ConfigFileStem}'.");

    public static string FromFolderName(string name)
    {
        var id = new StringBuilder();
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (!char.IsAsciiLetterOrDigit(character))
            {
                AppendHyphen(id);
                continue;
            }

            if (char.IsUpper(character) && index > 0 && StartsWord(name, index))
                AppendHyphen(id);
            id.Append(char.ToLowerInvariant(character));
        }

        var text = id.ToString().Trim('-');
        return Reserved.Contains(text) ? $"{text}-feature" : text;
    }

    static bool StartsWord(string name, int index)
    {
        var previous = name[index - 1];
        var nextIsLower = index + 1 < name.Length && char.IsLower(name[index + 1]);
        return char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower);
    }

    static void AppendHyphen(StringBuilder id)
    {
        if (id.Length > 0 && id[^1] != '-')
            id.Append('-');
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidPattern();
}
