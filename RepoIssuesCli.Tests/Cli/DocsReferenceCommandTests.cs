using System.Text.Json;
using RepoIssuesCli.Features.Issues;
using Spectre.Console.Testing;

namespace RepoIssuesCli.Tests.Cli;

public class DocsReferenceCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    const string Billing = """
                           feature: billing
                           status: in-progress
                           entries:
                             # hand-written comment
                             - id: rollout
                               kind: plan
                               summary: Roll out
                               steps:
                                 - title: Schema and store
                                   issues: [1, 2]
                                 - title: Cut over
                                   issues:
                                     - 1   # keep me
                           """;

    const string Repo = """
                        entries:
                          - id: cleanup
                            kind: plan
                            summary: Tidy up
                            steps:
                              - title: Remove dead code
                                issues: [2]
                        """;

    string DocsFile(string fileName) => Path.Combine(repo.Root, ".repo-docs", fileName);

    void WriteDocs()
    {
        repo.Write(".repo-docs/billing.yaml", Billing);
        repo.Write(".repo-docs/repo.yaml", Repo);
    }

    [Test]
    public void RenumberRewritesDocsSteps()
    {
        repo.Add("A");
        repo.Add("B");
        WriteDocs();

        var result = repo.Run("renumber", "A");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("Updated docs steps:").And.Contain("billing/rollout (Schema and store)").And.Contain("billing/rollout (Cut over)"));
        Assert.That(File.ReadAllText(DocsFile("billing.yaml")), Is.EqualTo(Billing.Replace("[1, 2]", "[3, 2]").Replace("- 1   # keep me", "- 3   # keep me")));
        Assert.That(File.ReadAllText(DocsFile("repo.yaml")), Is.EqualTo(Repo));
    }

    [Test]
    public void RenumberReportsDocsRewritesInJson()
    {
        repo.Add("A");
        repo.Add("B");
        WriteDocs();

        var result = repo.Run("renumber", "B", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var renumbered = IssueJson.Deserialize<RenumberResult>(result.Output)!;
        Assert.That(renumbered.Issue.Number, Is.EqualTo(3));
        Assert.That(renumbered.DocsReferences.Select(reference => (reference.Address, reference.StepTitle, reference.IssueNumber, reference.File)), Is.EqualTo(new[]
        {
            ("billing/rollout", "Schema and store", 3, ".repo-docs/billing.yaml"),
            ("repo/cleanup", "Remove dead code", 3, ".repo-docs/repo.yaml"),
        }));
        Assert.That(renumbered.KeptDocsReferences, Is.Empty);
        Assert.That(JsonDocument.Parse(result.Output).RootElement.GetProperty("keptDocsReferences").GetArrayLength(), Is.Zero);
        Assert.That(File.ReadAllText(DocsFile("repo.yaml")), Does.Contain("issues: [3]"));
    }

    [Test]
    public void RenumberOfADuplicateKeepsDocsStepsWithTheIssueThatKeepsTheNumber()
    {
        repo.Add("A");
        var duplicate = repo.Add("B");
        var store = repo.Store;
        store.Load();
        store.Save(duplicate with { Number = 1 });
        WriteDocs();

        var json = repo.Run("renumber", duplicate.Id.ToString(), "--json");

        Assert.That(json.ExitCode, Is.Zero, json.Errors);
        var renumbered = IssueJson.Deserialize<RenumberResult>(json.Output)!;
        Assert.That(renumbered.Issue.Number, Is.EqualTo(2));
        Assert.That(renumbered.DocsReferences, Is.Empty);
        Assert.That(renumbered.KeptDocsReferences.Select(reference => reference.Describe()),
            Is.EqualTo(new[] { "billing/rollout (Schema and store)", "billing/rollout (Cut over)" }));
        Assert.That(File.ReadAllText(DocsFile("billing.yaml")), Is.EqualTo(Billing));
    }

    [Test]
    public void RenumberOfADuplicateExplainsTheKeptDocsSteps()
    {
        repo.Add("A");
        var duplicate = repo.Add("B");
        var store = repo.Store;
        store.Load();
        store.Save(duplicate with { Number = 1 });
        WriteDocs();

        var result = repo.Run("renumber", duplicate.Id.ToString());

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("Docs steps on 0001 still mean 0001 A").And.Contain("docs edit <address> --step-issues"));
    }

    [Test]
    public void RenumberWithoutDocsFolderLeavesItAbsent()
    {
        repo.Add("A");

        var result = repo.Run("renumber", "A", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(JsonDocument.Parse(result.Output).RootElement.GetProperty("docsReferences").GetArrayLength(), Is.Zero);
        Assert.That(Directory.Exists(Path.Combine(repo.Root, ".repo-docs")), Is.False);
    }

    [Test]
    public void DeleteWarnsAboutDocsStepsAndLeavesThem()
    {
        repo.Add("A");
        repo.Add("B");
        WriteDocs();

        var result = repo.Run("delete", "B", "--force");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Errors, Does.Contain("Warning").And.Contain("billing/rollout (Schema and store)").And.Contain("repo/cleanup (Remove dead code)"));
        Assert.That(result.Output, Does.Contain("Deleted"));
        Assert.That(File.ReadAllText(DocsFile("billing.yaml")), Is.EqualTo(Billing));
    }

    [Test]
    public void DeleteListsDocsStepsInJson()
    {
        repo.Add("A");
        WriteDocs();

        var result = repo.Run("delete", "A", "--force", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var docs = JsonDocument.Parse(result.Output).RootElement.GetProperty("docs");
        Assert.That(docs.EnumerateArray().Select(reference => reference.GetProperty("stepTitle").GetString()), Is.EqualTo(new[] { "Schema and store", "Cut over" }));
        Assert.That(result.Errors, Does.Contain("billing/rollout"));
    }

    [Test]
    public void DeleteOfADuplicateLeavesDocsStepsToTheOtherIssue()
    {
        repo.Add("A");
        var duplicate = repo.Add("B");
        var store = repo.Store;
        store.Load();
        store.Save(duplicate with { Number = 1 });
        WriteDocs();

        var result = repo.Run("delete", duplicate.Id.ToString(), "--force", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(JsonDocument.Parse(result.Output).RootElement.GetProperty("docs").GetArrayLength(), Is.Zero);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void InteractiveDeleteShowsDocsStepsInTheConfirmation()
    {
        repo.Add("A");
        WriteDocs();
        var console = new TestConsole().Width(250).Interactive();
        console.Input.PushTextWithEnter("n");

        var result = repo.Run(console, "delete", "A");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("Docs plan steps link it").And.Contain("billing/rollout (Cut over)").And.Contain("Cancelled"));
        Assert.That(repo.Issues, Has.Count.EqualTo(1));
    }

    [Test]
    public void ShowListsDocsSteps()
    {
        repo.Add("A");
        repo.Add("B");
        WriteDocs();

        var text = repo.Run("show", "B");
        var json = repo.Run("show", "B", "--json");

        Assert.That(text.ExitCode, Is.Zero, text.Errors);
        Assert.That(text.Output, Does.Contain("Docs").And.Contain("billing/rollout (Schema and store)").And.Contain("repo/cleanup (Remove dead code)"));
        var docs = JsonDocument.Parse(json.Output).RootElement.GetProperty("docs").EnumerateArray().ToList();
        Assert.That(docs.Select(reference => reference.GetProperty("address").GetString()), Is.EqualTo(new[] { "billing/rollout", "repo/cleanup" }));
        Assert.That(docs[1].GetProperty("issueNumber").GetInt32(), Is.EqualTo(2));
        Assert.That(docs[1].GetProperty("file").GetString(), Is.EqualTo(".repo-docs/repo.yaml"));
        Assert.That(IssueJson.Deserialize<Issue>(json.Output)!.Name, Is.EqualTo("B"));
    }

    [Test]
    public void ShowJsonEmitsAnEmptyDocsArray()
    {
        repo.Add("A");

        var result = repo.Run("show", "A", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var docs = JsonDocument.Parse(result.Output).RootElement.GetProperty("docs");
        Assert.That(docs.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(docs.GetArrayLength(), Is.Zero);
        Assert.That(result.Output, Does.EndWith("}\n"));
    }

    [Test]
    public void InvalidDocsFilesAreWarnedAboutAndSkipped()
    {
        repo.Add("A");
        WriteDocs();
        repo.Write(".repo-docs/broken.yaml", "entries: [\n");

        var result = repo.Run("show", "A", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Errors, Does.Contain(".repo-docs/broken.yaml").And.Contain("docs file skipped"));
        Assert.That(JsonDocument.Parse(result.Output).RootElement.GetProperty("docs").GetArrayLength(), Is.EqualTo(2));
    }
}