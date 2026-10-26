namespace RepoDocsCli.Features.Docs;

public static class FeatureFolders
{
    const string FeaturesFolderName = "Features";

    static readonly HashSet<string> SkippedFolders = ["bin", "obj", "node_modules"];

    public static IReadOnlyList<Feature> Prefill(string root) =>
        Find(root)
            .Select(folder => (Id: FeatureId.FromFolderName(Path.GetFileName(folder)), Path: RepoPath(root, folder)))
            .Where(found => FeatureId.IsValid(found.Id))
            .GroupBy(found => found.Id)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Feature
            {
                Id = group.Key,
                Status = FeatureStatus.Shipped,
                When = new FeatureWhen { Paths = group.Select(found => found.Path).Order(StringComparer.Ordinal).ToList() },
            })
            .ToList();

    public static IEnumerable<string> Find(string root)
    {
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(root)]);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Children(directory))
            {
                if (child.Name == FeaturesFolderName)
                {
                    foreach (var feature in Children(child))
                        yield return feature.FullName;
                }

                pending.Push(child);
            }
        }
    }

    static IEnumerable<DirectoryInfo> Children(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateDirectories()
                .Where(child => !child.Name.StartsWith('.')
                                && !SkippedFolders.Contains(child.Name)
                                && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToList();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    static string RepoPath(string root, string folder) => Path.GetRelativePath(root, folder).Replace('\\', '/') + "/";
}
