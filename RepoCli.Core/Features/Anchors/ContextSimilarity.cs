using System.Text.RegularExpressions;

namespace RepoCli.Core.Features.Anchors;

static partial class ContextSimilarity
{
    public const double Threshold = 0.7;

    public static string[] Tokenize(string line) => [.. Token.Matches(line).Select(match => match.Value)];

    /// <summary>Weighted Jaccard over token counts: shared tokens divided by all tokens.</summary>
    public static double Of(IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
    {
        var shared = 0;
        var total = 0;
        foreach (var (token, count) in left)
        {
            var other = right.GetValueOrDefault(token);
            shared += Math.Min(count, other);
            total += Math.Max(count, other);
        }

        foreach (var (token, count) in right)
            if (!left.ContainsKey(token))
                total += count;

        return total == 0 ? 0 : (double)shared / total;
    }

    [GeneratedRegex(@"\w+|[^\w\s]")]
    private static partial Regex Token { get; }
}
