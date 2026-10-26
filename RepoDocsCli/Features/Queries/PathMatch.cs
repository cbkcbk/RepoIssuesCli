using System.Text.RegularExpressions;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public static class PathMatch
{
    const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    public static bool HasLocation(WhenHints? hints) => hints is not null && (hints.Paths.Count > 0 || hints.Anchors.Count > 0);

    public static bool Matches(WhenHints? hints, string path) =>
        hints is not null
        && (hints.Paths.Any(hint => Covers(hint, path)) || hints.Anchors.Any(anchor => Normalize(anchor.Path).Equals(path, Comparison)));

    public static bool Covers(string hint, string path)
    {
        var pattern = Normalize(hint);
        if (pattern.Length == 0)
            return false;
        if (pattern.AsSpan().IndexOfAny('*', '?') >= 0)
            return Glob(pattern, path);
        if (pattern.EndsWith('/'))
            return path.StartsWith(pattern, Comparison) || path.Equals(pattern.TrimEnd('/'), Comparison);

        return path.Equals(pattern, Comparison) || path.StartsWith(pattern + "/", Comparison);
    }

    public static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    static bool Glob(string pattern, string path)
    {
        if (pattern.EndsWith('/'))
            pattern += "**";

        var target = pattern.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..];
        var expression = Regex.Escape(pattern)
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return Regex.IsMatch(target, $"^{expression}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
