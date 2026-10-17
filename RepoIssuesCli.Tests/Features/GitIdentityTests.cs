using System.Diagnostics;
using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Features;

public class GitIdentityTests
{
    [TestCase("Ada Lovelace <ada@example.com> 1760000000 +0200\n", "Ada Lovelace <ada@example.com>")]
    [TestCase("Ada Lovelace <> 1760000000 +0200", "Ada Lovelace")]
    [TestCase("garbage", null)]
    public void ParsesAuthorIdent(string ident, string? expected) =>
        Assert.That(GitIdentity.ParseIdent(ident), Is.EqualTo(expected));

    [Test]
    public void ReadsAuthorFromRepoConfig()
    {
        using var repo = new TempRepo();
        Git(repo.Root, "init", "-q");
        Git(repo.Root, "config", "user.name", "Test User");
        Git(repo.Root, "config", "user.email", "test@example.com");

        Assert.That(GitIdentity.Author(repo.Root), Is.EqualTo("Test User <test@example.com>"));
    }

    public static void Git(string directory, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = directory })!;
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, $"git {string.Join(' ', args)}");
    }
}
