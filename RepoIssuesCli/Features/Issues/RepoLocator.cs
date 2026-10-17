namespace RepoIssuesCli.Features.Issues;

public static class RepoLocator
{
    public const string IssuesFolderName = ".repo-issues";

    public static string FindRoot(string? explicitRoot, string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot, startDirectory);

        var start = Path.GetFullPath(startDirectory);
        return FindAncestorContaining(start, IssuesFolderName)
            ?? FindAncestorContaining(start, ".git")
            ?? start;
    }

    static string? FindAncestorContaining(string start, string entryName)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, entryName);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return directory.FullName;
        }

        return null;
    }
}
