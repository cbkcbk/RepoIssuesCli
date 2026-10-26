using RepoDocsCli.Cli;
using RepoDocsCli.Features.Docs;
using Spectre.Console.Testing;

namespace RepoDocsCli.Tests;

public sealed record RunResult(int ExitCode, string Output, string Errors);

public sealed class TempRepo : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("docs-tests-").FullName;

    public DocsStore Store => new(Root);

    public string Folder => Store.Folder;

    public RunResult Run(params string[] args) => RunWithoutRoot([.. args, "--root", Root]);

    public RunResult RunWithoutRoot(params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = DocsApp.Create(output, errors).Run(args);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    public RunResult RunIn(string directory, params string[] args)
    {
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        try
        {
            return RunWithoutRoot(args);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    public string Subfolder(params string[] parts) => Directory.CreateDirectory(Path.Combine([Root, .. parts])).FullName;

    public string ReadDocsFile(string fileName) => File.ReadAllText(Path.Combine(Folder, fileName));

    public void WriteDocsFile(string fileName, string content)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, fileName), content);
    }

    public void InitGit()
    {
        Git("init", "-q");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
    }

    public string Git(params string[] args)
    {
        var result = RepoCli.Core.Features.Repo.Git.Run(Root, args);
        Assert.That(result?.ExitCode, Is.Zero, $"git {string.Join(' ', args)}");
        return result!.Output;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
