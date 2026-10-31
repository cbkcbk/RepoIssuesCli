using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Apps;

namespace RepoInfraCli.Tests;

public sealed class GitRemote : IDisposable
{
    public GitRemote(string name = "platform-infra")
    {
        Bare = Path.Combine(Root, $"{name}.git");
        Git(Root, "init", "-q", "-b", "main", "work");
        Git(Root, "init", "-q", "--bare", Bare);
        Git(Bare, "config", "uploadpack.allowFilter", "true");
        Git(Bare, "config", "uploadpack.allowAnySHA1InWant", "true");
        Git(Work, "remote", "add", "origin", Bare);
    }

    public string Root { get; } = Directory.CreateTempSubdirectory("infra-remote-").FullName;

    public string Bare { get; private set; }

    public string Url => new Uri(Bare).AbsoluteUri;

    string Work => Path.Combine(Root, "work");

    public GitRemote WriteInfra(string script, string folder = RepoLocator.InfraFolderName) =>
        Write($"{folder}/{InfraWorkspace.EntryFileName}", script);

    public GitRemote Write(string relativePath, string content)
    {
        var full = Path.Combine(Work, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n"));
        return this;
    }

    public string Commit(string message = "change")
    {
        Git(Work, "add", "-A");
        Git(Work, "-c", "user.email=t@example.test", "-c", "user.name=t", "-c", "commit.gpgsign=false", "commit", "-q", "-m", message);
        Git(Work, "push", "-q", "origin", "HEAD:main");
        return Git(Work, "rev-parse", "HEAD").Trim();
    }

    public void Tag(string name)
    {
        Git(Work, "tag", "-f", name);
        Git(Work, "push", "-q", "-f", "origin", $"refs/tags/{name}");
    }

    public void TakeOffline()
    {
        var moved = Bare + ".offline";
        Directory.Move(Bare, moved);
        Bare = moved;
    }

    static string Git(string folder, params string[] arguments)
    {
        var result = ProcessRunner.Instance.RunAsync("git", arguments, folder).GetAwaiter().GetResult();
        Assert.That(result.Succeeded, Is.True, $"git {string.Join(' ', arguments)}: {result.Tail()}");
        return result.Stdout;
    }

    public static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    public void Dispose() => Delete(Root);
}
