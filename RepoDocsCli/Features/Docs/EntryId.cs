using System.Text;

namespace RepoDocsCli.Features.Docs;

public static class EntryId
{
    public const char Separator = '/';
    public const int MaxDerivedLength = 40;
    const string FallbackId = "entry";

    public static bool IsValid(string? id) => FeatureId.IsKebabCase(id);

    public static string Address(string feature, string entry) => $"{feature}{Separator}{entry}";

    public static string FromSummary(string summary, IEnumerable<string> taken) =>
        Unique(FromSummary(summary), taken.ToHashSet(StringComparer.Ordinal));

    public static string FromSummary(string summary)
    {
        var words = Words(summary);
        var slug = new StringBuilder();
        foreach (var word in words)
        {
            if (slug.Length > 0 && slug.Length + 1 + word.Length > MaxDerivedLength)
                break;
            if (slug.Length > 0)
                slug.Append('-');
            slug.Append(word.Length > MaxDerivedLength ? word[..MaxDerivedLength] : word);
        }

        return slug.Length > 0 ? slug.ToString() : FallbackId;
    }

    public static string Unique(string id, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(id))
            return id;

        var suffix = 2;
        while (taken.Contains($"{id}-{suffix}"))
            suffix++;
        return $"{id}-{suffix}";
    }

    static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (character is '\'' or '’')
                continue;
            if (char.IsAsciiLetterOrDigit(character))
            {
                word.Append(character);
                continue;
            }

            if (word.Length > 0)
                words.Add(word.ToString());
            word.Clear();
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return words;
    }
}