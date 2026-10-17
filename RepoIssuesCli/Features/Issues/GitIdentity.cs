using System.ComponentModel;
using System.Diagnostics;

namespace RepoIssuesCli.Features.Issues;

public static class GitIdentity
{
    public static string? Author(string workingDirectory)
    {
        var start = new ProcessStartInfo("git", ["var", "GIT_AUTHOR_IDENT"])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? ParseIdent(output) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public static string? ParseIdent(string ident)
    {
        var end = ident.LastIndexOf('>');
        if (end < 0)
            return null;

        var author = ident[..(end + 1)].Replace("<>", "").Trim();
        return author.Length == 0 ? null : author;
    }
}
