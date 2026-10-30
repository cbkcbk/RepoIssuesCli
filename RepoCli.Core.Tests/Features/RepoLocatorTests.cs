namespace RepoCli.Core.Tests.Features;

public class RepoLocatorTests
{
    TempFolder folder = null!;

    [SetUp]
    public void SetUp() => folder = new TempFolder();

    [TearDown]
    public void TearDown() => folder.Dispose();

    [TestCase(RepoLocator.IssuesFolderName)]
    [TestCase(RepoLocator.DocsFolderName)]
    [TestCase(RepoLocator.InfraFolderName)]
    [TestCase(".git")]
    public void EachToolFolderMarksTheRoot(string marker)
    {
        folder.Subfolder(marker);

        Assert.That(RepoLocator.FindRoot(null, folder.Subfolder("src", "deep")), Is.EqualTo(folder.Root));
    }

    [Test]
    public void DocsAndIssuesResolveToTheSameRootFromASubfolder()
    {
        folder.Subfolder(".git");
        folder.Subfolder(RepoLocator.IssuesFolderName);
        folder.Subfolder(RepoLocator.DocsFolderName);

        Assert.That(RepoLocator.FindRoot(null, folder.Subfolder("a", "b")), Is.EqualTo(folder.Root));
    }

    [Test]
    public void OutsideAnyRepoFallsBackToStartDirectory()
    {
        var start = folder.Subfolder("a", "b");

        Assert.That(RepoLocator.FindRoot(null, start), Is.EqualTo(start));
    }

    [Test]
    public void ExplicitRootWins() =>
        Assert.That(RepoLocator.FindRoot("other", folder.Root), Is.EqualTo(Path.Combine(folder.Root, "other")));
}