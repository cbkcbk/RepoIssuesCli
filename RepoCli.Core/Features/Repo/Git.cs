using System.ComponentModel;
using System.Diagnostics;

namespace RepoCli.Core.Features.Repo;

public sealed record GitResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

public static class Git
{
    public static GitResult? Run(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git", arguments)
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

            var errorOutput = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            errorOutput.Wait();
            process.WaitForExit();
            return new GitResult(process.ExitCode, output);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public static void Add(string root, string path) =>
        Run(root, "add", "--", Path.GetRelativePath(root, path));

    public static void Move(string root, string source, string destination)
    {
        if (Run(root, "mv", "--", Path.GetRelativePath(root, source), Path.GetRelativePath(root, destination)) is not { Succeeded: true })
            File.Move(source, destination);
    }

    public static void Remove(string root, string path)
    {
        Run(root, "rm", "-f", "-q", "--ignore-unmatch", "--", Path.GetRelativePath(root, path));
        if (File.Exists(path))
            File.Delete(path);
    }
}
