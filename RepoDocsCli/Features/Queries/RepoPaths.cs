namespace RepoDocsCli.Features.Queries;

public sealed class RepoPaths(string root)
{
    static readonly HashSet<string> SkippedFolders = new(["bin", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase);

    readonly Lazy<IReadOnlyList<string>> files = new(() => ListFiles(root));

    public bool Exists(string hint)
    {
        var normalized = PathMatch.Normalize(hint);
        if (PathMatch.IsGlob(normalized))
            return files.Value.Any(file => PathMatch.Covers(normalized, file));

        var full = Path.Combine(root, normalized.TrimEnd('/'));
        return Directory.Exists(full) || (!normalized.EndsWith('/') && File.Exists(full));
    }

    static IReadOnlyList<string> ListFiles(string root) =>
        Git.Run(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard") is { Succeeded: true } result
            ? result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(file => File.Exists(Path.Combine(root, file))).ToList()
            : Walk(root, root).ToList();

    static IEnumerable<string> Walk(string root, string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            yield return Path.GetRelativePath(root, file).Replace('\\', '/');

        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || SkippedFolders.Contains(name))
                continue;
            foreach (var file in Walk(root, child))
                yield return file;
        }
    }
}
