namespace RepoCli.Core.Features.Repo;

public static class RepoLocator
{
    public const string IssuesFolderName = ".repo-issues";
    public const string DocsFolderName = ".repo-docs";
    public const string InfraFolderName = ".repo-infra";

    static readonly string[] RootMarkers = [IssuesFolderName, DocsFolderName, InfraFolderName, ".git"];

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