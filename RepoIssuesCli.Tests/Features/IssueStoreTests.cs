using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Features;

public class IssueStoreTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    static Issue NewIssue(int number, string name = "Issue") => new()
    {
        Id = Guid.CreateVersion7(),
        Number = number,
        Name = name,
        Created = Issue.Timestamp(),
        Updated = Issue.Timestamp(),
    };

    string[] IssueFiles() =>
        Directory.EnumerateFiles(repo.Store.Folder, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Test]
    public void SavesFileNamedByNumberAndNameAndLoadsItBack()
    {
        var issue = NewIssue(1, "Login fails") with { Tags = ["bug"], BlockedBy = [2], Locations = [new SourceLocation("a.cs", 3, 4)] };
        repo.Store.Save(issue);

        Assert.That(IssueFiles(), Is.EqualTo(new[] { "00001-login_fails.json" }));
        var loaded = repo.Issues.Single();
        Assert.That(IssueJson.Serialize(loaded), Is.EqualTo(IssueJson.Serialize(issue)));
    }

    [TestCase(15, "Find .repo-issues from the git root", "00015-find_repo_issues_from_the_git_root.json")]
    [TestCase(3, "  Fix: æøå -- Ölrückgabe!  ", "00003-fix_æøå_ölrückgabe.json")]
    [TestCase(7, "?!", "00007.json")]
    [TestCase(12345, "x", "12345-x.json")]
    public void FileNameIsNumberAndSnakeCaseName(int number, string name, string expected) =>
        Assert.That(IssueStore.FileNameFor(NewIssue(number, name)), Is.EqualTo(expected));

    [Test]
    public void LongNamesAreCutAtAWordBoundary()
    {
        var fileName = IssueStore.FileNameFor(NewIssue(15, "Find .repo-issues from the git root when run in a subfolder of the repository"));

        Assert.That(fileName, Is.EqualTo("00015-find_repo_issues_from_the_git_root_when_run_in_a_subfolder.json"));
    }

    [Test]
    public void LongNamesWithoutSpacesAreCutHard()
    {
        var fileName = IssueStore.FileNameFor(NewIssue(1, new string('a', 80)));

        Assert.That(fileName, Is.EqualTo($"00001-{new string('a', 60)}.json"));
    }

    [Test]
    public void SavingARenamedIssueMovesItsFile()
    {
        var store = repo.Store;
        var issue = NewIssue(1, "Old name");
        store.Save(issue);
        store.Save(issue with { Name = "New name" });

        Assert.That(IssueFiles(), Is.EqualTo(new[] { "00001-new_name.json" }));
    }

    [Test]
    public void SavingOntoAnotherIssuesFileThrows()
    {
        var store = repo.Store;
        store.Save(NewIssue(1, "Taken"));
        var other = NewIssue(1, "Other");
        store.Save(other);

        Assert.Throws<IssueFileConflictException>(() => store.Save(other with { Name = "Taken" }));
    }

    [Test]
    public void LoadRenamesLegacyIdFiles()
    {
        repo.Store.Initialize();
        var issue = NewIssue(4, "Legacy");
        File.WriteAllText(Path.Combine(repo.Store.Folder, $"{issue.Id:D}.json"), IssueJson.Serialize(issue));

        Assert.That(repo.Issues.Single().Id, Is.EqualTo(issue.Id));
        Assert.That(IssueFiles(), Is.EqualTo(new[] { "00004-legacy.json" }));
    }

    [Test]
    public void OmitsEmptyCollectionsAndNullsAndUsesStringEnums()
    {
        var issue = NewIssue(1);
        repo.Store.Save(issue);

        var json = File.ReadAllText(repo.Store.PathFor(issue));
        Assert.That(json, Does.Not.Contain("tags").And.Not.Contain("blockedBy").And.Not.Contain("description"));
        Assert.That(json, Does.Contain("\"status\": \"Open\"").And.Contain("\"priority\": \"Medium\""));
        Assert.That(json, Does.EndWith("\n"));
    }

    [Test]
    public void NextNumberFollowsHighestExisting()
    {
        repo.Store.Save(NewIssue(1));
        repo.Store.Save(NewIssue(7));

        Assert.That(repo.Store.Load().NextNumber(), Is.EqualTo(8));
    }

    [Test]
    public void ReportsInvalidFilesInsteadOfThrowing()
    {
        repo.Store.Initialize();
        File.WriteAllText(Path.Combine(repo.Store.Folder, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(repo.Store.Folder, "empty.json"), "null");

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Issues, Is.Empty);
        Assert.That(snapshot.Errors, Has.Count.EqualTo(2));
    }

    [Test]
    public void LocatorPrefersIssuesFolderThenGitThenStart()
    {
        var nested = Directory.CreateDirectory(Path.Combine(repo.Root, "a", "b")).FullName;
        Assert.That(RepoLocator.FindRoot(null, nested), Is.EqualTo(nested));

        Directory.CreateDirectory(Path.Combine(repo.Root, ".git"));
        Assert.That(RepoLocator.FindRoot(null, nested), Is.EqualTo(repo.Root));

        Directory.CreateDirectory(Path.Combine(repo.Root, "a", ".repo-issues"));
        Assert.That(RepoLocator.FindRoot(null, nested), Is.EqualTo(Path.Combine(repo.Root, "a")));
    }
}
