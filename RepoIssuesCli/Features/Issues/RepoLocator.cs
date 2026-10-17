namespace RepoIssuesCli.Features.Issues;

public static class RepoLocator
{
    public const string IssuesFolderName = ".repo-issues";

    static readonly string[] RootMarkers = [IssuesFolderName, ".git"];

    public static string FindRoot(string? explicitRoot, string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot, startDirectory);

        var start = Path.GetFullPath(startDirectory);
        return FindNearestMarkedAncestor(start) ?? start;
    }

    static string? FindNearestMarkedAncestor(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (RootMarkers.Any(marker => Path.Exists(Path.Combine(directory.FullName, marker))))
                return directory.FullName;
        }

        return null;
    }
}
