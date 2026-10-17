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

    [Test]
    public void SavesFileNamedByIdAndLoadsItBack()
    {
        var issue = NewIssue(1) with { Tags = ["bug"], BlockedBy = [2], Locations = [new SourceLocation("a.cs", 3, 4)] };
        repo.Store.Save(issue);

        Assert.That(File.Exists(Path.Combine(repo.Root, ".repo-issues", $"{issue.Id:D}.json")), Is.True);
        var loaded = repo.Issues.Single();
        Assert.That(IssueJson.Serialize(loaded), Is.EqualTo(IssueJson.Serialize(issue)));
    }

    [Test]
    public void OmitsEmptyCollectionsAndNullsAndUsesStringEnums()
    {
        var issue = NewIssue(1);
        repo.Store.Save(issue);

        var json = File.ReadAllText(repo.Store.PathFor(issue.Id));
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
        var mismatched = NewIssue(2);
        File.WriteAllText(Path.Combine(repo.Store.Folder, $"{Guid.CreateVersion7():D}.json"), IssueJson.Serialize(mismatched));

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
