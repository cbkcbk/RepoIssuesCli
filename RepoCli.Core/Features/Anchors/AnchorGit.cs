using RepoCli.Core.Features.Repo;

namespace RepoCli.Core.Features.Anchors;

/// <summary>Git queries for the resolver. Every method returns empty or null when git is missing or fails.</summary>
static class AnchorGit
{
    /// <summary>Renames between HEAD and the working tree, including staged <c>git mv</c>.</summary>
    public static IReadOnlyDictionary<string, string> WorkingRenames(string root) =>
        Renames(Git.Run(root, "diff", "-M", "--name-status", "-z", "--relative", "HEAD"));

    /// <summary>Where <paramref name="path"/> went in the last commit that touched it.</summary>
    public static string? CommittedRename(string root, string path)
    {
        if (Git.Run(root, "log", "-n1", "--format=%H", "--", path) is not { Succeeded: true } log
            || log.Output.Trim() is not { Length: > 0 } commit)
            return null;

        return Renames(Git.Run(root, "diff", "-M", "--name-status", "-z", "--relative", commit + "^", commit)).GetValueOrDefault(path);
    }

    public static IReadOnlyList<string> Commits(string root, string path, int depth) =>
        Git.Run(root, "log", $"-n{depth}", "--format=%H", "--", path) is { Succeeded: true } result
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>File content at <paramref name="revision"/>; an empty revision reads the index.</summary>
    public static string? Show(string root, string revision, string path) =>
        Git.Run(root, "show", $"{revision}:./{path}") is { Succeeded: true } result ? result.Output : null;

    static Dictionary<string, string> Renames(GitResult? result)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result is not { Succeeded: true })
            return renames;

        var fields = result.Output.Split('\0');
        for (var index = 0; index < fields.Length;)
        {
            var status = fields[index];
            if (status.StartsWith('R') || status.StartsWith('C'))
            {
                if (status.StartsWith('R') && index + 2 < fields.Length)
                    renames[fields[index + 1]] = fields[index + 2];
                index += 3;
            }
            else
                index += 2;
        }

        return renames;
    }
}
