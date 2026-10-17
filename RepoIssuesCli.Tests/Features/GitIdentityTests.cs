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
        repo.InitGit();

        Assert.That(GitIdentity.Author(repo.Root), Is.EqualTo("Test User <test@example.com>"));
    }
}
