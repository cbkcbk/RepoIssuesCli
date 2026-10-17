using RepoIssuesCli.Cli;
using RepoIssuesCli.Features.Issues;
using Spectre.Console.Testing;

namespace RepoIssuesCli.Tests.Cli;

public class CommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    [Test]
    public void InitCreatesFolderOnce()
    {
        Assert.That(repo.Run("init").ExitCode, Is.Zero);
        Assert.That(Directory.Exists(Path.Combine(repo.Root, ".repo-issues")), Is.True);
        Assert.That(repo.Run("init").Output, Does.Contain("Already initialized"));
    }

    [Test]
    public void NewWithFlagsWritesAllFields()
    {
        var blocker = repo.Add("Blocker");
        var location = Path.Combine(repo.Root, "src", "a.cs") + ":3:5";

        var issue = repo.Add("Second", "-d", "Text", "-p", "high", "-s", "in-progress", "-t", "bug,auth", "-t", "Bug", "-b", "Blocker", "-l", location);

        Assert.That(issue.Number, Is.EqualTo(2));
        Assert.That(issue.Title, Is.EqualTo("0002 Second"));
        Assert.That(issue.Description, Is.EqualTo("Text"));
        Assert.That(issue.Priority, Is.EqualTo(IssuePriority.High));
        Assert.That(issue.Status, Is.EqualTo(IssueStatus.InProgress));
        Assert.That(issue.Tags, Is.EqualTo(new[] { "bug", "auth" }));
        Assert.That(issue.BlockedBy, Is.EqualTo(new[] { blocker.Number }));
        Assert.That(issue.Locations, Is.EqualTo(new[] { new SourceLocation("src/a.cs", 3, 5) }));
        Assert.That(repo.Issues, Has.Count.EqualTo(2));
    }

    [Test]
    public void NewDefaultsAuthorToGitAuthor()
    {
        repo.InitGit();

        Assert.That(repo.Add("A").Author, Is.EqualTo("Test User <test@example.com>"));
        Assert.That(repo.Add("B", "--author", "Someone Else").Author, Is.EqualTo("Someone Else"));
    }

    [Test]
    public void EditAndListByAuthor()
    {
        repo.Add("A", "--author", "Ada");
        repo.Add("B", "--author", "Grace");

        Assert.That(repo.Run("edit", "B", "--author", "Grace Hopper").ExitCode, Is.Zero);

        Assert.That(ListNames("list", "--author", "hopper"), Is.EqualTo(new[] { "B" }));
        Assert.That(repo.Run("show", "B").Output, Does.Contain("Grace Hopper"));
    }

    [Test]
    public void NewWithoutNameFailsWhenNotInteractive()
    {
        var result = repo.Run("new");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("[name]"));
        Assert.That(repo.Issues, Is.Empty);
    }

    [Test]
    public void NewPromptsForNameWhenInteractive()
    {
        var console = new TestConsole().Width(250).Interactive();
        console.Input.PushTextWithEnter("Prompted issue");
        console.Input.PushTextWithEnter("");
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("ui, ux");

        var result = repo.Run(console, "new");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var issue = repo.Issues.Single();
        Assert.That(issue.Name, Is.EqualTo("Prompted issue"));
        Assert.That(issue.Description, Is.Null);
        Assert.That(issue.Priority, Is.EqualTo(IssuePriority.High));
        Assert.That(issue.Tags, Is.EqualTo(new[] { "ui", "ux" }));
    }

    [Test]
    public void InvalidEnumGivesReadableUsageError()
    {
        var result = repo.Run("new", "X", "-p", "urgent");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("Low, Medium, High, Critical"));
    }

    [Test]
    public void ListHidesClosedAndFilters()
    {
        repo.Add("Open bug", "-t", "bug");
        repo.Add("Done thing", "-s", "Done");
        repo.Add("Blocked", "-b", "1");

        Assert.That(ListNames("list"), Is.EqualTo(new[] { "Open bug", "Blocked" }));
        Assert.That(ListNames("list", "--all"), Has.Count.EqualTo(3));
        Assert.That(ListNames("list", "-s", "done"), Is.EqualTo(new[] { "Done thing" }));
        Assert.That(ListNames("list", "-t", "BUG"), Is.EqualTo(new[] { "Open bug" }));
        Assert.That(ListNames("list", "--ready"), Is.EqualTo(new[] { "Open bug" }));
        Assert.That(ListNames("list", "--blocked"), Is.EqualTo(new[] { "Blocked" }));
    }

    [Test]
    public void ListTableShowsTitlesAndBlockers()
    {
        repo.Add("First");
        repo.Add("Second", "-b", "1");

        var output = repo.Run("list").Output;

        Assert.That(output, Does.Contain("0001").And.Contain("First").And.Contain("Second"));
    }

    [Test]
    public void ShowResolvesBlockerNamesAfterRename()
    {
        repo.Add("First");
        repo.Add("Second", "-b", "1");

        Assert.That(repo.Run("edit", "1", "--name", "Renamed").ExitCode, Is.Zero);

        var output = repo.Run("show", "Second").Output;
        Assert.That(output, Does.Contain("0001 Renamed"));
    }

    [Test]
    public void EditNameRenamesTheFile()
    {
        repo.Add("First");

        Assert.That(repo.Run("edit", "1", "--name", "Renamed issue").ExitCode, Is.Zero);

        Assert.That(IssueFiles(), Is.EqualTo(new[] { "00001-renamed_issue.json" }));
    }

    [Test]
    public void EditOntoAnotherIssuesFileNameIsAUsageError()
    {
        repo.Add("Taken");
        var other = repo.Add("Other");
        var store = repo.Store;
        store.Load();
        store.Save(other with { Number = 1 });

        var result = repo.Run("edit", other.Id.ToString(), "--name", "Taken");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("00001-taken.json"));
    }

    [Test]
    public void GitTracksNewRenamedAndDeletedIssues()
    {
        repo.InitGit();
        repo.Add("First");
        repo.Add("Second");
        Assert.That(repo.Git("ls-files"), Does.Contain(".repo-issues/00001-first.json").And.Contain(".repo-issues/00002-second.json"));

        repo.Git("commit", "-q", "-m", "issues");
        Assert.That(repo.Run("edit", "1", "--name", "Renamed").ExitCode, Is.Zero);
        Assert.That(repo.Git("status", "--porcelain"), Does.Contain("RM .repo-issues/00001-first.json -> .repo-issues/00001-renamed.json"));

        Assert.That(repo.Run("delete", "2", "--force").ExitCode, Is.Zero);
        Assert.That(repo.Git("ls-files"), Does.Not.Contain("00002-second.json"));
    }

    string[] IssueFiles() =>
        Directory.EnumerateFiles(repo.Store.Folder, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Test]
    public void ShowUnknownIssueReturnsNotFound() =>
        Assert.That(repo.Run("show", "42").ExitCode, Is.EqualTo(ExitCodes.NotFound));

    [Test]
    public void EditAppliesAddAndRemoveFlags()
    {
        repo.Add("A");
        repo.Add("B");
        var root = repo.Root;
        repo.Add("Target", "-t", "one,two", "-b", "1", "-l", Path.Combine(root, "x.cs:1"), "-l", Path.Combine(root, "x.cs:9"), "-l", Path.Combine(root, "y.cs"));

        var result = repo.Run("edit", "Target", "--json", "--remove-tag", "ONE", "--add-tag", "three",
            "--remove-blocker", "1", "--add-blocker", "B", "--remove-location", Path.Combine(root, "x.cs"),
            "--priority", "critical", "-d", "New text");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var issue = IssueJson.Deserialize<Issue>(result.Output)!;
        Assert.That(issue.Tags, Is.EqualTo(new[] { "two", "three" }));
        Assert.That(issue.BlockedBy, Is.EqualTo(new[] { 2 }));
        Assert.That(issue.Locations, Is.EqualTo(new[] { new SourceLocation("y.cs") }));
        Assert.That(issue.Priority, Is.EqualTo(IssuePriority.Critical));
        Assert.That(issue.Description, Is.EqualTo("New text"));
    }

    [Test]
    public void EditRejectsCycles()
    {
        repo.Add("A");
        repo.Add("B", "-b", "1");

        var result = repo.Run("edit", "1", "--add-blocker", "2");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("cycle"));
        Assert.That(repo.Issues.Single(issue => issue.Number == 1).BlockedBy, Is.Empty);
    }

    [Test]
    public void EditMenuChangesStatusInteractively()
    {
        repo.Add("A");
        var console = new TestConsole().Width(250).Interactive();
        PushDown(console, 3);
        PushDown(console, 1);
        PushDown(console, 8);

        var result = repo.Run(console, "edit", "1");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(repo.Issues.Single().Status, Is.EqualTo(IssueStatus.InProgress));
    }

    static void PushDown(TestConsole console, int times)
    {
        for (var i = 0; i < times; i++)
            console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
    }

    [Test]
    public void EditWithoutChangesFailsWhenNotInteractive()
    {
        repo.Add("A");

        Assert.That(repo.Run("edit", "1").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void StatusChangesStatusAndTimestamp()
    {
        var created = repo.Add("A");

        var result = repo.Run("status", "A", "done", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var issue = IssueJson.Deserialize<Issue>(result.Output)!;
        Assert.That(issue.Status, Is.EqualTo(IssueStatus.Done));
        Assert.That(issue.Updated, Is.GreaterThanOrEqualTo(created.Updated));
    }

    [Test]
    public void DeleteRequiresForceWhenNotInteractive()
    {
        repo.Add("A");

        Assert.That(repo.Run("delete", "1").ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(repo.Issues, Has.Count.EqualTo(1));
    }

    [Test]
    public void DeleteCleansBlockerReferences()
    {
        repo.Add("A");
        repo.Add("B", "-b", "1");

        Assert.That(repo.Run("delete", "A", "--force").ExitCode, Is.Zero);

        Assert.That(repo.Issues.Single().BlockedBy, Is.Empty);
    }

    [Test]
    public void ValidateReportsProblemsWithExitCode()
    {
        repo.Add("A");
        Assert.That(repo.Run("validate").ExitCode, Is.Zero);

        var store = repo.Store;
        var issue = store.Load().Issues.Single();
        store.Save(issue with { BlockedBy = [7] });
        var result = repo.Run("validate", "--json");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(result.Output, Does.Contain("\"kind\": \"DanglingBlocker\""));
    }

    [Test]
    public void JsonOutputIsPureJson()
    {
        repo.Add("A long name that would otherwise wrap in a narrow console because it keeps going and going");

        var output = repo.Run(new TestConsole().Width(20), "list", "--json").Output;

        Assert.That(IssueJson.Deserialize<List<Issue>>(output), Has.Count.EqualTo(1));
    }

    List<string> ListNames(params string[] args)
    {
        var result = repo.Run([.. args, "--json"]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return IssueJson.Deserialize<List<Issue>>(result.Output)!.Select(issue => issue.Name).ToList();
    }
}
