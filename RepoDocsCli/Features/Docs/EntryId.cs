using System.Text;

namespace RepoDocsCli.Features.Docs;

public static class EntryId
{
    public const char Separator = '/';
    public const int MaxDerivedLength = 40;
    const string Fallback = "entry";

    public static bool IsValid(string? id) => FeatureId.IsKebabCase(id);

    public static string Address(string feature, string entry) => $"{feature}{Separator}{entry}";

    public static string FromSummary(string summary, IEnumerable<string> taken)
    {
        var words = Words(summary);
        var id = new StringBuilder();
        foreach (var word in words)
        {
            if (id.Length > 0 && id.Length + 1 + word.Length > MaxDerivedLength)
                break;
            if (id.Length > 0)
                id.Append('-');
            id.Append(word.Length > MaxDerivedLength ? word[..MaxDerivedLength] : word);
        }

        var stem = id.Length == 0 ? Fallback : id.ToString();
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var candidate = stem;
        for (var suffix = 2; used.Contains(candidate); suffix++)
            candidate = $"{stem}-{suffix}";
        return candidate;
    }

    static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var character in text)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (character == '\'' || word.Length == 0)
                continue;
            yield return word.ToString();
            word.Clear();
        }

        if (word.Length > 0)
            yield return word.ToString();
    }
}
