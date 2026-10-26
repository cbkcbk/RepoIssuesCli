using System.Text.Json;
using RepoDocsCli.Cli;
using RepoDocsCli.Features.Docs;
using Spectre.Console.Testing;

namespace RepoDocsCli.Tests.Cli;

public class EditCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.Run("init");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Money.cs"), AddCommandTests.Money);
        Ok("add", "billing", "--summary", "Invoices", "--path", "src/", "--keyword", "invoice");
        Ok("add", "billing/rounding", "--kind", "rule", "--summary", "Round half to even", "--body", "Use ToEven.",
            "--path", "src/Money.cs", "--anchor", "src/Money.cs:5-8", "--keyword", "rounding");
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    FeatureEntry Rounding(string id = "rounding") => repo.Store.LoadFeature("billing").Entries.Single(entry => entry.Id == id);

    [Test]
    public void EditChangesEntryTextFields()
    {
        var result = Ok("edit", "billing/rounding", "--summary", "Round once", "--kind", "gotcha", "--body", "Only at the end.");

        Assert.That(result.Output, Is.EqualTo("Updated billing/rounding (.repo-docs/billing.yaml)\n"));
        var entry = Rounding();
        Assert.That((entry.Summary, entry.Kind, entry.Body), Is.EqualTo(("Round once", "gotcha", "Only at the end.")));

        Ok("edit", "billing/rounding", "--clear-body");
        Assert.That(Rounding().Body, Is.Null);

        var bodyFile = Path.Combine(repo.Root, "body.md");
        File.WriteAllText(bodyFile, "Line one.\nLine two.\n");
        Ok("edit", "billing/rounding", "--body-file", bodyFile);
        Assert.That(Rounding().Body, Is.EqualTo("Line one.\nLine two."));
    }

    [Test]
    public void EditAddsAndRemovesEntryHints()
    {
        Ok("edit", "billing/rounding", "--remove-path", "src/Money.cs", "--add-path", "src", "--remove-keyword", "ROUNDING", "--add-keyword", "money,vat",
            "--remove-anchor", "Money.Round", "--add-anchor", "src/Money.cs:7");

        var when = Rounding().When!;
        Assert.That(when.Paths, Is.EqualTo(new[] { "src/" }));
        Assert.That(when.Keywords, Is.EqualTo(new[] { "money", "vat" }));
        Assert.That(when.Anchors.Single().Line, Is.EqualTo(7));

        Ok("edit", "billing/rounding", "--remove-path", "src/", "--remove-keyword", "money", "--remove-keyword", "vat", "--remove-anchor", "src/Money.cs:7");
        Assert.That(Rounding().When, Is.Null);
    }

    [TestCase("--remove-anchor", "src/Money.cs")]
    [TestCase("--remove-anchor", "src/Money.cs:5")]
    [TestCase("--remove-anchor", "Money.Round")]
    public void AnchorsAreRemovedByPathLineOrSymbol(string option, string value)
    {
        Ok("edit", "billing/rounding", option, value);

        Assert.That(Rounding().When!.Anchors, Is.Empty);
    }

    [Test]
    public void AddingAnAnchorOnTheSameLineReplacesIt()
    {
        var old = Rounding().When!.Anchors.Single();
        File.WriteAllText(Path.Combine(repo.Root, "src", "Money.cs"), AddCommandTests.Money.Replace("ToEven);", "AwayFromZero);"));

        Ok("edit", "billing/rounding", "--add-anchor", "src/Money.cs:5-8");

        var anchor = Rounding().When!.Anchors.Single();
        Assert.That(anchor.Body, Is.Not.EqualTo(old.Body));
        Assert.That(anchor.Context, Is.EqualTo(old.Context));
        Assert.That(Rounding().Updated, Is.Not.Null);
    }

    [TestCase("--remove-path", "docs/")]
    [TestCase("--remove-keyword", "nope")]
    [TestCase("--remove-anchor", "Other.Method")]
    public void RemovingAHintThatIsNotThereIsAUsageError(string option, string value)
    {
        var result = repo.Run("edit", "billing/rounding", option, value);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("matches"));
    }

    [Test]
    public void EditChangesFeatureFields()
    {
        Ok("edit", "billing", "--status", "shipped", "--summary", "Invoices and payments", "--add-path", "docs/billing.md", "--remove-keyword", "invoice");

        var feature = repo.Store.LoadFeature("billing");
        Assert.That(feature.Status, Is.EqualTo(FeatureStatus.Shipped));
        Assert.That(feature.Summary, Is.EqualTo("Invoices and payments"));
        Assert.That(feature.When.Paths, Is.EqualTo(new[] { "src/", "docs/billing.md" }));
        Assert.That(feature.When.Keywords, Is.Empty);
        Assert.That(feature.Updated, Is.Not.Null);

        Ok("edit", "billing", "--clear-summary");
        Assert.That(repo.Store.LoadFeature("billing").Summary, Is.Null);
    }

    [Test]
    public void EditBumpsUpdatedButANoOpEditChangesNothing()
    {
        var before = repo.ReadDocsFile("billing.yaml");
        Assert.That(Rounding().Updated, Is.Null);

        var unchanged = Ok("edit", "billing/rounding", "--summary", "Round half to even", "--kind", "rule");

        Assert.That(unchanged.Output, Is.EqualTo("Unchanged billing/rounding (.repo-docs/billing.yaml)\n"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));

        Ok("edit", "billing/rounding", "--summary", "Round once");
        Assert.That(Rounding().Updated, Is.Not.Null);
        Assert.That(repo.Store.LoadFeature("billing").Updated, Is.Null);
    }

    [Test]
    public void FeatureRenameUsesGitMv()
    {
        repo.InitGit();
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "docs");

        var json = Json("edit", "billing", "--id", "invoicing", "--status", "shipped", "--json");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("invoicing"));
        Assert.That(json.GetProperty("feature").GetProperty("feature").GetString(), Is.EqualTo("invoicing"));
        Assert.That(repo.Store.Exists("billing"), Is.False);
        Assert.That(repo.Store.LoadFeature("invoicing").Status, Is.EqualTo(FeatureStatus.Shipped));
        Assert.That(repo.Store.LoadFeature("invoicing").Entries.Single().Id, Is.EqualTo("rounding"));
        Assert.That(repo.Git("diff", "--cached", "--name-status", "-M"), Does.Match(@"R\d*\s+\.repo-docs/billing\.yaml\s+\.repo-docs/invoicing\.yaml"));
    }

    [Test]
    public void FeatureRenameToAnExistingFeatureIsAUsageError()
    {
        Ok("add", "search");

        var result = repo.Run("edit", "billing", "--id", "search");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(repo.Store.Exists("billing"), Is.True);
    }

    [Test]
    public void EntryRenameKeepsTheEntryInItsFile()
    {
        var created = Rounding().Created;

        var json = Json("edit", "billing/rounding", "--id", "money-rounding", "--json");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("billing/money-rounding"));
        var entry = Rounding("money-rounding");
        Assert.That(entry.Created, Is.EqualTo(created));
        Assert.That(entry.Updated, Is.Null);
        Assert.That(entry.Summary, Is.EqualTo("Round half to even"));
    }

    [TestCase("Bad_Id")]
    [TestCase("vat")]
    public void EntryRenameToAnInvalidOrTakenIdIsAUsageError(string id)
    {
        Ok("add", "billing/vat", "--kind", "fact", "--summary", "VAT is 25%");

        var result = repo.Run("edit", "billing/rounding", "--id", id);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(Rounding().Id, Is.EqualTo("rounding"));
    }

    [Test]
    public void PlanStepsCanBeAddedReplacedLinkedAndRemoved()
    {
        Ok("add", "billing/rollout", "--kind", "plan", "--summary", "Roll out", "--step", "Schema #23", "--step", "Store", "--step", "Guide");

        Ok("edit", "billing/rollout", "--set-step", "1=Schema and store #23 #24", "--step-issues", "2=25,#26", "--remove-step", "3", "--add-step", "Release #30");

        var steps = Rounding("rollout").Steps;
        Assert.That(steps.Select(DocsFormat), Is.EqualTo(new[] { "Schema and store: 23,24", "Store: 25,26", "Release: 30" }));

        Ok("edit", "billing/rollout", "--step-issues", "1=");
        Assert.That(Rounding("rollout").Steps[0].Issues, Is.Empty);
    }

    [TestCase("--remove-step", "4")]
    [TestCase("--set-step", "0=Nope")]
    [TestCase("--step-issues", "x=1")]
    [TestCase("--step-issues", "1=abc")]
    public void BadStepReferencesAreUsageErrors(string option, string value)
    {
        Ok("add", "billing/rollout", "--kind", "plan", "--summary", "Roll out", "--step", "Schema");

        Assert.That(repo.Run("edit", "billing/rollout", option, value).ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void StepsOnANonPlanEntryAreRejected()
    {
        var result = repo.Run("edit", "billing/rounding", "--add-step", "Do it");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("only 'plan' entries may have steps"));
        Assert.That(Rounding().Steps, Is.Empty);
    }

    [Test]
    public void ChangingAPlanWithStepsToAnotherKindIsRejected()
    {
        Ok("add", "billing/rollout", "--kind", "plan", "--summary", "Roll out", "--step", "Schema");

        var result = repo.Run("edit", "billing/rollout", "--kind", "decision");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(Rounding("rollout").Kind, Is.EqualTo("plan"));
    }

    [TestCase("--kind", "note")]
    [TestCase("--summary", "Two\nlines")]
    [TestCase("--summary", " ")]
    public void InvalidEntryChangesAreRejected(string option, string value)
    {
        var before = repo.ReadDocsFile("billing.yaml");

        var result = repo.Run("edit", "billing/rounding", option, value);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage), result.Errors);
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    [TestCase("billing", "--kind", "rule")]
    [TestCase("billing", "--body", "Text")]
    [TestCase("billing/rounding", "--status", "shipped")]
    [TestCase("billing/rounding", "--clear-summary", null)]
    [TestCase("repo", "--add-path", "src/")]
    public void OptionsForTheOtherTargetTypeAreUsageErrors(string target, string option, string? value)
    {
        var result = repo.Run(value is null ? ["edit", target, option] : ["edit", target, option, value]);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void NoChangesIsAUsageErrorWhenNotInteractive()
    {
        var result = repo.Run("edit", "billing/rounding");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("No changes given"));
    }

    [Test]
    public void MissingTargetIsAUsageErrorWhenNotInteractive() =>
        Assert.That(repo.Run("edit", "--summary", "X").Errors, Does.Contain("Missing required input: [target]"));

    [TestCase("missing")]
    [TestCase("missing/rounding")]
    [TestCase("billing/missing")]
    [TestCase("repo/missing")]
    public void MissingTargetsAreNotFound(string target)
    {
        var result = repo.Run("edit", target, "--summary", "X");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.NotFound));
        Assert.That(result.Errors, Does.Contain("not found"));
    }

    [Test]
    public void RepoSummaryAndRepoEntriesCanBeEdited()
    {
        Ok("add", "repo/style", "--kind", "rule", "--summary", "Use file-scoped namespaces");

        var repoJson = Json("edit", "repo", "--summary", "Monorepo for the shop", "--json");
        Ok("edit", "repo/style", "--add-keyword", "style");

        Assert.That(repoJson.GetProperty("address").GetString(), Is.EqualTo("repo"));
        Assert.That(repoJson.GetProperty("repo").GetProperty("summary").GetString(), Is.EqualTo("Monorepo for the shop"));
        var docs = repo.Store.Load().Repo;
        Assert.That(docs.Summary, Is.EqualTo("Monorepo for the shop"));
        Assert.That(docs.Entries.Single().When!.Keywords, Is.EqualTo(new[] { "style" }));
    }

    [Test]
    public void EditJsonReportsTheResultingEntry()
    {
        var json = Json("edit", "billing/rounding", "--summary", "Round once", "--json");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("billing/rounding"));
        Assert.That(json.GetProperty("file").GetString(), Is.EqualTo(".repo-docs/billing.yaml"));
        Assert.That(json.GetProperty("entry").GetProperty("summary").GetString(), Is.EqualTo("Round once"));
        Assert.That(json.GetProperty("entry").GetProperty("updated").GetDateTimeOffset(), Is.EqualTo(Rounding().Updated));
    }

    [Test]
    public void InteractiveEditUsesTheFieldMenu()
    {
        var output = new TestConsole().Width(250).Interactive();
        output.Input.PushKey(ConsoleKey.DownArrow);
        output.Input.PushKey(ConsoleKey.DownArrow);
        output.Input.PushKey(ConsoleKey.Enter);
        output.Input.PushTextWithEnter("Round once");
        for (var step = 0; step < 7; step++)
            output.Input.PushKey(ConsoleKey.DownArrow);
        output.Input.PushKey(ConsoleKey.Enter);

        var exitCode = DocsApp.Create(output, new TestConsole()).Run(["edit", "billing/rounding", "--root", repo.Root]);

        Assert.That(exitCode, Is.Zero, output.Output);
        Assert.That(output.Output, Does.Contain("Updated billing/rounding"));
        Assert.That(Rounding().Summary, Is.EqualTo("Round once"));
    }

    static string DocsFormat(PlanStep step) => $"{step.Title}: {string.Join(',', step.Issues)}";

    RunResult Ok(params string[] args)
    {
        var result = repo.Run(args);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return result;
    }

    JsonElement Json(params string[] args) => JsonDocument.Parse(Ok(args).Output).RootElement;
}
