namespace RepoIssuesCli.Features.Issues;

public static class GitIdentity
{
    public static string? Author(string workingDirectory) =>
        Git.Run(workingDirectory, "var", "GIT_AUTHOR_IDENT") is { Succeeded: true } result ? ParseIdent(result.Output) : null;

    public static string? ParseIdent(string ident)
    {
        var end = ident.LastIndexOf('>');
        if (end < 0)
            return null;

        var author = ident[..(end + 1)].Replace("<>", "").Trim();
        return author.Length == 0 ? null : author;
    }
}
