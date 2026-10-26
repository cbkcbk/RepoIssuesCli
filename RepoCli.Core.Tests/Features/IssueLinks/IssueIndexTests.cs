using RepoCli.Core.Features.IssueLinks;

namespace RepoCli.Core.Tests.Features.IssueLinks;

public class IssueIndexTests
{
    TempFolder folder = null!;

    [SetUp]
    public void SetUp() => folder = new TempFolder();

    [TearDown]
    public void TearDown() => folder.Dispose();

    void WriteIssueFile(string fileName, string json) =>
        File.WriteAllText(Path.Combine(folder.Subfolder(RepoLocator.IssuesFolderName), fileName), json);

    const string ReadmeSample = """
        {
          "id": "01a14aaf-2636-76c1-8f28-fed5d98d1f75",
          "number": 1,
          "name": "Login fails on empty password",
          "description": "Optional text",
          "author": "Ada Lovelace <ada@example.com>",
          "status": "Open",
          "priority": "High",
          "blockedBy": [
            3,
            7
          ],
          "locations": [{ "path": "src/Auth/Login.cs", "line": 42, "column": 9 }],
          "tags": ["bug"],
          "created": "2026-10-17T16:25:54+00:00",
          "updated": "2026-10-17T16:31:02+00:00",
          "comments": [{ "author": "Grace Hopper <grace@example.com>", "created": "2026-10-17T16:31:02+00:00", "text": "Reproduced on main" }]
        }
        """;

    const string MinimalInProgress = """
        {
          "id": "01a14ac3-6d44-74e6-b11f-852a3f553cb2",
          "number": 3,
          "name": "Hash passwords",
          "status": "InProgress",
          "priority": "Medium",
          "created": "2026-10-17T16:48:03+00:00",
          "updated": "2026-10-17T16:48:03+00:00"
        }
        """;

    [Test]
    public void LoadsIssuesWrittenInTheIssuesFileFormat()
    {
        WriteIssueFile("00003-hash_passwords.json", MinimalInProgress);
        WriteIssueFile("00001-login_fails_on_empty_password.json", ReadmeSample);
        WriteIssueFile("00007-done.json", """{ "number": 7, "name": "Done", "status": "Done" }""");

        var index = IssueIndex.Load(folder.Root);

        Assert.That(index.IsInitialized, Is.True);
        Assert.That(index.Errors, Is.Empty);
        Assert.That(index.Issues.Select(issue => issue.Number), Is.EqualTo(new[] { 1, 3, 7 }));
        var login = index.Find(1)!;
        Assert.That(login.Name, Is.EqualTo("Login fails on empty password"));
        Assert.That(login.State, Is.EqualTo(IssueState.Open));
        Assert.That(login.BlockedBy, Is.EqualTo(new[] { 3, 7 }));
        Assert.That(login.FileName, Is.EqualTo("00001-login_fails_on_empty_password.json"));
        Assert.That(index.Find(3)!.State, Is.EqualTo(IssueState.InProgress));
        Assert.That(index.Find(3)!.BlockedBy, Is.Empty);
        Assert.That(index.Find(2), Is.Null);
    }

    [Test]
    public void MissingIssuesFolderGivesAnEmptyIndex()
    {
        var index = IssueIndex.Load(folder.Root);

        Assert.That(index.IsInitialized, Is.False);
        Assert.That(index.Issues, Is.Empty);
        Assert.That(index.Errors, Is.Empty);
    }

    [Test]
    public void InvalidFilesAreReportedNotThrown()
    {
        WriteIssueFile("00001-good.json", """{ "number": 1, "name": "Good" }""");
        WriteIssueFile("broken.json", "{ not json");
        WriteIssueFile("empty.json", "null");
        WriteIssueFile("status.json", """{ "number": 2, "name": "Odd", "status": "Finished" }""");
        WriteIssueFile("number.json", """{ "name": "No number" }""");
        WriteIssueFile("blockers.json", """{ "number": 4, "name": "Bad", "blockedBy": ["three"] }""");
        WriteIssueFile(".gitkeep", "");

        var index = IssueIndex.Load(folder.Root);

        Assert.That(index.Issues.Select(issue => issue.Name), Is.EqualTo(new[] { "Good" }));
        Assert.That(index.Find(1)!.State, Is.EqualTo(IssueState.Open));
        Assert.That(index.Errors.Select(error => error.FileName),
            Is.EquivalentTo(new[] { "blockers.json", "broken.json", "empty.json", "number.json", "status.json" }));
    }

    [Test]
    public void StatusIsCaseInsensitive()
    {
        WriteIssueFile("00001-a.json", """{ "number": 1, "name": "A", "status": "cancelled" }""");

        Assert.That(IssueIndex.Load(folder.Root).Find(1)!.State, Is.EqualTo(IssueState.Cancelled));
    }

    [Test]
    public void UnresolvedBlockersAreOpenOrMissingIssues()
    {
        var index = new IssueIndex(
        [
            new IssueSummary(1, "Blocked", IssueState.Open, [2, 3, 4, 5], "a.json"),
            new IssueSummary(2, "Open blocker", IssueState.Open, [], "b.json"),
            new IssueSummary(3, "Done blocker", IssueState.Done, [], "c.json"),
            new IssueSummary(4, "Cancelled blocker", IssueState.Cancelled, [], "d.json"),
            new IssueSummary(6, "Free", IssueState.Open, [3], "e.json"),
        ], []);

        Assert.That(index.UnresolvedBlockers(index.Find(1)!), Is.EqualTo(new[] { 2, 5 }));
        Assert.That(index.IsBlocked(index.Find(1)!), Is.True);
        Assert.That(index.IsBlocked(index.Find(6)!), Is.False);
    }

    [Test]
    public void DuplicateNumbersResolveToTheFirstFile()
    {
        WriteIssueFile("00001-first.json", """{ "number": 1, "name": "First" }""");
        WriteIssueFile("00001-second.json", """{ "number": 1, "name": "Second" }""");

        var index = IssueIndex.Load(folder.Root);

        Assert.That(index.Issues, Has.Count.EqualTo(2));
        Assert.That(index.Find(1)!.Name, Is.EqualTo("First"));
    }
}
