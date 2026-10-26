using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Import;

public static class ImportSources
{
    public static readonly string[] FileNames = ["CLAUDE.md", "AGENTS.md"];

    public static IReadOnlyList<string> Find(string root)
    {
        var nested = FeatureFolders.Directories(root)
            .SelectMany(folder => FileNames.Select(name => Path.Combine(folder, name)))
            .Where(File.Exists)
            .OrderBy(path => RepoPath(root, path), StringComparer.Ordinal);

        return [.. FileNames.Select(name => Path.Combine(root, name)).Where(File.Exists), .. nested];
    }

    public static IReadOnlyList<string> Resolve(string root, IReadOnlyList<string> files, string currentDirectory) =>
        files.Count == 0
            ? Find(root)
            : files.Select(file => Require(root, file, Path.GetFullPath(file, currentDirectory))).Distinct().ToList();

    public static string RepoPath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string FolderOf(string repoPath) => repoPath.LastIndexOf('/') is var slash and >= 0 ? repoPath[..(slash + 1)] : "";

    static string Require(string root, string argument, string path)
    {
        if (!File.Exists(path))
            throw new UsageException($"File not found: {argument}");

        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UsageException($"{argument} is outside the repository root {root}.");

        return path;
    }
}
