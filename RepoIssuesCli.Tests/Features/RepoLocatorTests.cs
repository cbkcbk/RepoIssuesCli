using RepoIssuesCli.Cli;
using RepoIssuesCli.Features.Issues;
using Spectre.Console.Testing;

namespace RepoIssuesCli.Tests.Features;

public class RepoLocatorTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    string Subfolder(params string[] parts) =>
        Directory.CreateDirectory(Path.Combine([repo.Root, .. parts])).FullName;

    [Test]
    public void SubfolderOfGitRepoWithIssuesFolderFindsGitRoot()
    {
        repo.InitGit();
        repo.Store.Initialize();

        Assert.That(RepoLocator.FindRoot(null, Subfolder("src", "deep")), Is.EqualTo(repo.Root));
    }

    [Test]
    public void SubfolderOfUninitializedGitRepoFindsGitRoot()
    {
        repo.InitGit();

        Assert.That(RepoLocator.FindRoot(null, Subfolder("src", "deep")), Is.EqualTo(repo.Root));
    }

    [Test]
    public void GitFileMarksRootLikeInWorktreesAndSubmodules()
    {
        File.WriteAllText(Path.Combine(repo.Root, ".git"), "gitdir: /elsewhere/.git/worktrees/wt\n");

        Assert.That(RepoLocator.FindRoot(null, Subfolder("src")), Is.EqualTo(repo.Root));
    }

    [Test]
    public void NestedRepoStopsAtNearestRepoRoot()
    {
        repo.InitGit();
        repo.Store.Initialize();
        var nested = Subfolder("modules", "inner");
        File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: ../../.git/modules/inner\n");

        Assert.That(RepoLocator.FindRoot(null, Subfolder("modules", "inner", "src")), Is.EqualTo(nested));
    }

    [Test]
    public void OutsideAnyRepoFallsBackToStartDirectory()
    {
        var start = Subfolder("a", "b");

        Assert.That(RepoLocator.FindRoot(null, start), Is.EqualTo(start));
    }

    [Test]
    public void ExplicitRootWins()
    {
        repo.InitGit();

        Assert.That(RepoLocator.FindRoot("other", repo.Root), Is.EqualTo(Path.Combine(repo.Root, "other")));
    }

    [Test]
    public void InitFromSubfolderCreatesIssuesFolderAtGitRoot()
    {
        repo.InitGit();
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Subfolder("src", "deep");
        try
        {
            var exitCode = IssuesApp.Create(new TestConsole(), new TestConsole()).Run(["init"]);
            Assert.That(exitCode, Is.Zero);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.That(Directory.Exists(Path.Combine(repo.Root, RepoLocator.IssuesFolderName)), Is.True);
        Assert.That(Directory.Exists(Path.Combine(repo.Root, "src", "deep", RepoLocator.IssuesFolderName)), Is.False);
    }
}
