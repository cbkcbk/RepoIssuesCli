namespace RepoCli.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("repocli-tests-").FullName;

    public string Subfolder(params string[] parts) => Directory.CreateDirectory(Path.Combine([Root, .. parts])).FullName;

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