using System.Text.Json.Nodes;
using RepoDocsCli.Cli;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Hooks;
using Spectre.Console.Testing;

namespace RepoDocsCli.Tests.Cli;

public class HookCommandTests
{
    const string InvoicePath = "src/Billing/Invoice.cs";
    static readonly DateTimeOffset Stamp = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly string LongBody = string.Join(' ', Enumerable.Repeat("Long explanation.", 60));

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.InitGit();
        repo.Store.Initialize();
        WriteSource(InvoicePath, "class Invoice;\n");
        WriteSource("lib/Other.cs", "class Other;\n");
        Prepare(FeatureStatus.Shipped);
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    void WriteSource(string path, string content)
    {
        var full = Path.Combine(repo.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void Commit()
    {
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "commit");
    }

    static FeatureEntry Entry(string id, string kind, string? body = null) =>
        new() { Id = id, Kind = kind, Summary = $"{id} summary", Body = body, Created = Stamp, Updated = Stamp };

    void Prepare(FeatureStatus status)
    {
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(new Feature
        {
            Id = "billing",
            Status = status,
            Summary = "Invoices",
            When = new WhenHints { Paths = ["src/Billing/"] },
            Created = Stamp,
            Updated = Stamp,
            Entries =
            [
                Entry("layout", "fact", "Invoices live in src/Billing."),
                Entry("rounding", "rule", "Round half to even."),
                Entry("why", "decision", "Because."),
                Entry("rollout", "plan"),
                Entry("long", "fact", LongBody),
            ],
        }));
        Commit();
    }

    static RunResult RunWithInput(string hookEvent, string stdin, params string[] options)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = DocsApp.Create(output, errors, new StringReader(stdin)).Run(["hook", hookEvent, .. options]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    RunResult Hook(string hookEvent, string? filePath = null, string session = "session-1", bool stopHookActive = false)
    {
        var input = new JsonObject
        {
            ["session_id"] = session,
            ["cwd"] = repo.Root,
            ["hook_event_name"] = "PostToolUse",
            ["stop_hook_active"] = stopHookActive,
        };
        if (filePath is not null)
            input["tool_input"] = new JsonObject { ["file_path"] = Path.Combine(repo.Root, filePath) };
        return RunWithInput(hookEvent, input.ToJsonString());
    }

    static string Context(RunResult result)
    {
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var output = JsonNode.Parse(result.Output)!["hookSpecificOutput"]!;
        Assert.That(output["hookEventName"]!.GetValue<string>(), Is.EqualTo("PostToolUse"));
        return output["additionalContext"]!.GetValue<string>();
    }

    static void AssertSilent(RunResult result)
    {
        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Output, Is.Empty);
        Assert.That(result.Errors, Is.Empty);
    }

    FeatureEntry Stored(string id) => repo.Store.LoadFeature("billing").Entries.Single(entry => entry.Id == id);

    [Test]
    public void ReadPushesOnReadKindsOnly()
    {
        var context = Context(Hook("read", InvoicePath));

        Assert.That(context, Does.Contain("billing/layout (fact): layout summary").And.Contain("Invoices live in src/Billing."));
        Assert.That(context, Does.Not.Contain("billing/rounding").And.Not.Contain("billing/why").And.Not.Contain("billing/rollout"));
    }

    [Test]
    public void EditPushesOnEditAndOnReadKinds()
    {
        var context = Context(Hook("edit", InvoicePath));

        Assert.That(context, Does.Contain("billing/rounding (rule)").And.Contain("Round half to even.").And.Contain("billing/layout"));
        Assert.That(context, Does.Not.Contain("billing/why").And.Not.Contain("billing/rollout"));
    }

    [Test]
    public void PlansArePushedOnlyWhileTheFeatureIsInProgress()
    {
        Prepare(FeatureStatus.InProgress);

        Assert.That(Context(Hook("read", InvoicePath)), Does.Contain("billing/rollout (plan)"));
    }

    [Test]
    public void LongBodyPushesOnlyTheSummaryAndIsNotMarkedRead()
    {
        var context = Context(Hook("read", InvoicePath));

        Assert.That(context, Does.Contain("billing/long (fact): long summary").And.Contain("`docs show billing/long`"));
        Assert.That(context, Does.Not.Contain("Long explanation."));
        Assert.That(Stored("long").Read, Is.Null);
    }

    [Test]
    public void FullBodyPushMarksTheRead()
    {
        Hook("read", InvoicePath);

        Assert.That(Stored("layout").Read, Is.EqualTo(DateOnly.FromDateTime(DateTime.UtcNow)));
        Assert.That(Stored("layout").Updated, Is.EqualTo(Stamp));
    }

    [Test]
    public void EntriesAreNotPushedTwiceInOneSession()
    {
        Context(Hook("read", InvoicePath));

        AssertSilent(Hook("read", InvoicePath));
        Assert.That(Context(Hook("edit", InvoicePath)), Does.Contain("billing/rounding").And.Not.Contain("billing/layout"));
        Assert.That(Context(Hook("read", InvoicePath, session: "session-2")), Does.Contain("billing/layout"));
    }

    [Test]
    public void SessionCacheIsIgnoredByGit()
    {
        Hook("read", InvoicePath);

        Assert.That(File.Exists(HookSession.PathOf(repo.Folder, "session-1")), Is.True);
        Assert.That(repo.Git("status", "--porcelain", "--untracked-files=all"), Does.Not.Contain(HookSession.FolderName));
    }

    [Test]
    public void FileOutsideAnyFeaturePrintsNothing() => AssertSilent(Hook("read", "lib/Other.cs"));

    [Test]
    public void FileOutsideTheRepositoryPrintsNothing()
    {
        using var other = new TempRepo();

        var input = new JsonObject { ["session_id"] = "s", ["cwd"] = repo.Root, ["tool_input"] = new JsonObject { ["file_path"] = Path.Combine(other.Root, "x.cs") } };

        AssertSilent(RunWithInput("read", input.ToJsonString(), "--root", repo.Root));
    }

    [Test]
    public void ReadingADocsFileRemindsToUseTheCliOnce()
    {
        Assert.That(Context(Hook("read", ".repo-docs/billing.yaml")), Does.Contain("Never read or edit .repo-docs/ files directly"));
        AssertSilent(Hook("edit", ".repo-docs/billing.yaml"));
    }

    [Test]
    public void WithoutRepoDocsEveryEventIsSilent()
    {
        Directory.Delete(repo.Folder, recursive: true);

        foreach (var hookEvent in new[] { "session-start", "read", "edit", "stop" })
            AssertSilent(Hook(hookEvent, InvoicePath));
        Assert.That(Directory.Exists(repo.Folder), Is.False);
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[1, 2]")]
    [TestCase("""{ "session_id": 5, "tool_input": "x" }""")]
    public void MalformedInputIsSilent(string stdin)
    {
        AssertSilent(RunWithInput("read", stdin, "--root", repo.Root));
        AssertSilent(RunWithInput("stop", stdin, "--root", repo.Root));
    }

    [Test]
    public void SessionStartRecordsHead()
    {
        var head = repo.Git("rev-parse", "HEAD").Trim();

        AssertSilent(Hook("session-start"));
        WriteSource("lib/Other.cs", "class Other2;\n");
        Commit();
        AssertSilent(Hook("session-start"));

        Assert.That(HookSession.Read(repo.Folder, "session-1")?.Head, Is.EqualTo(head));
    }

    [Test]
    public void StopBlocksOnceWithAffectedDocsSinceTheSessionStarted()
    {
        Hook("session-start");
        WriteSource(InvoicePath, "class Invoice { }\n");
        Commit();

        var result = Hook("stop");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var output = JsonNode.Parse(result.Output)!;
        Assert.That(output["decision"]!.GetValue<string>(), Is.EqualTo("block"));
        Assert.That(output["reason"]!.GetValue<string>(),
            Does.Contain("- billing (feature): Invoices").And.Contain("- billing/layout (fact): layout summary").And.Contain("docs touch <address>"));
        Assert.That(output["reason"]!.GetValue<string>(), Does.Not.Contain("billing/why"));

        AssertSilent(Hook("stop"));
    }

    [Test]
    public void StopAllowsWhileAStopHookIsActive()
    {
        WriteSource(InvoicePath, "class Invoice { }\n");

        AssertSilent(Hook("stop", stopHookActive: true));
    }

    [Test]
    public void StopAllowsWhenNothingIsAffected()
    {
        Hook("session-start");
        WriteSource("lib/Other.cs", "class Other2;\n");

        AssertSilent(Hook("stop"));
    }
}
