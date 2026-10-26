using System.Text.Json;
using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Cli;

public class AddCommandTests
{
    public const string Money = "namespace Shop;\n\npublic class Money\n{\n    public decimal Round(decimal value)\n    {\n        return Math.Round(value, MidpointRounding.ToEven);\n    }\n}\n";

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.Run("init");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "Money.cs"), Money);
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    [Test]
    public void AddFeatureWritesTheFileAndStagesIt()
    {
        repo.InitGit();

        var result = repo.Run("add", "billing", "--status", "in-progress", "--summary", "Invoices and payments", "--path", "src", "--keyword", "invoice,vat");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Is.EqualTo("Created billing (.repo-docs/billing.yaml)\n"));
        var feature = repo.Store.LoadFeature("billing");
        Assert.That(feature.Status, Is.EqualTo(FeatureStatus.InProgress));
        Assert.That(feature.Summary, Is.EqualTo("Invoices and payments"));
        Assert.That(feature.When.Paths, Is.EqualTo(new[] { "src/" }));
        Assert.That(feature.When.Keywords, Is.EqualTo(new[] { "invoice", "vat" }));
        Assert.That(feature.Created, Is.Not.Null);
        Assert.That(feature.Updated, Is.Null);
        Assert.That(repo.Git("diff", "--cached", "--name-only"), Does.Contain(".repo-docs/billing.yaml"));
    }

    [Test]
    public void AddFeatureJsonIsTheFeatureWithItsAddress()
    {
        var json = Json("add", "billing", "--anchor", "src/Money.cs:5", "--json");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("billing"));
        Assert.That(json.GetProperty("file").GetString(), Is.EqualTo(".repo-docs/billing.yaml"));
        Assert.That(json.TryGetProperty("entry", out _), Is.False);
        var feature = json.GetProperty("feature");
        Assert.That(feature.GetProperty("feature").GetString(), Is.EqualTo("billing"));
        Assert.That(feature.GetProperty("status").GetString(), Is.EqualTo("proposed"));
        Assert.That(feature.GetProperty("when").GetProperty("anchors")[0].GetProperty("symbol").GetString(), Is.EqualTo("Money.Round"));
        Assert.That(feature.GetProperty("entries").GetArrayLength(), Is.Zero);
    }

    [TestCase("billing", ExitCodes.Usage)]
    [TestCase("repo", ExitCodes.Usage)]
    [TestCase("Not-Kebab", ExitCodes.Usage)]
    [TestCase("a/b/c", ExitCodes.Usage)]
    public void AddRejectsExistingOrInvalidFeatures(string target, int exitCode)
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", target);

        Assert.That(result.ExitCode, Is.EqualTo(exitCode));
        Assert.That(result.Errors, Does.Contain("Error:"));
    }

    [Test]
    public void AddEntryWithEveryHintType()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round half to even", "--body", "Use ToEven.\nNever round twice.",
            "--path", "src/Money.cs", "--anchor", "src/Money.cs:5-8", "--keyword", "rounding", "--keyword", "money");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Is.EqualTo("Created billing/rounding (.repo-docs/billing.yaml)\n"));
        var entry = repo.Store.LoadFeature("billing").Entries.Single();
        Assert.That(entry.Id, Is.EqualTo("rounding"));
        Assert.That(entry.Kind, Is.EqualTo("rule"));
        Assert.That(entry.Summary, Is.EqualTo("Round half to even"));
        Assert.That(entry.Body, Is.EqualTo("Use ToEven.\nNever round twice."));
        Assert.That(entry.When!.Paths, Is.EqualTo(new[] { "src/Money.cs" }));
        Assert.That(entry.When.Keywords, Is.EqualTo(new[] { "rounding", "money" }));
        Assert.That(entry.When.Anchors.Single().Path, Is.EqualTo("src/Money.cs"));
        Assert.That(entry.Created, Is.Not.Null);
        Assert.That(entry.Updated, Is.Null);
    }

    [Test]
    public void AnchorsGetTheSymbolAndHashesOfTheCurrentCode()
    {
        repo.Run("add", "billing");

        repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round half to even", "--anchor", "src/Money.cs:5-8");

        var anchor = repo.Store.LoadFeature("billing").Entries.Single().When!.Anchors.Single();
        var expected = Anchor.CreateWithSymbol("src/Money.cs", SourceText.Parse(Money), 5, 8);
        Assert.That(anchor, Is.EqualTo(expected));
        Assert.That(anchor.Symbol, Is.EqualTo("Money.Round"));
        Assert.That(anchor.Span, Is.EqualTo(4));
        Assert.That(anchor.Context, Has.Length.EqualTo(Fingerprint.HashLength));
        Assert.That(anchor.Body, Has.Length.EqualTo(Fingerprint.HashLength));
    }

    [Test]
    public void AnchorsAreRelativeToTheCurrentFolderInsideTheRepository()
    {
        repo.Run("add", "billing");

        var result = repo.RunIn(Path.Combine(repo.Root, "src"), "add", "billing/rounding", "--kind", "rule", "--summary", "Round", "--anchor", "Money.cs:7", "--path", ".");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var when = repo.Store.LoadFeature("billing").Entries.Single().When!;
        Assert.That(when.Anchors.Single().Path, Is.EqualTo("src/Money.cs"));
        Assert.That(when.Paths, Is.EqualTo(new[] { "src/" }));
    }

    [TestCase("src/Missing.cs:1")]
    [TestCase("src/Money.cs:0")]
    [TestCase("src/Money.cs:10")]
    [TestCase("src/Money.cs:5-12")]
    [TestCase("src/Money.cs:7-5")]
    [TestCase("src/Money.cs")]
    [TestCase("../outside.cs:1")]
    public void AnchorsToMissingFilesOrLinesAreUsageErrors(string anchor)
    {
        repo.Run("add", "billing");
        var before = repo.ReadDocsFile("billing.yaml");

        var result = repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round", "--anchor", anchor);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage), result.Errors);
        Assert.That(result.Errors, Does.Contain("anchor").IgnoreCase.Or.Contain("outside"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    [Test]
    public void UndeclaredKindIsAUsageError()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rounding", "--kind", "note", "--summary", "Round");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("Kind 'note' is not declared").And.Contain("rule, fact"));
        Assert.That(repo.Store.LoadFeature("billing").Entries, Is.Empty);
    }

    [Test]
    public void MultiLineSummaryIsRejectedByValidation()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round half\nto even");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("would make the docs invalid").And.Contain("summary over several lines"));
        Assert.That(repo.Store.LoadFeature("billing").Entries, Is.Empty);
    }

    [Test]
    public void MissingSummaryIsAUsageErrorWhenNotInteractive()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rounding", "--kind", "rule");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--summary"));
    }

    [Test]
    public void EntryOptionsOnAFeatureNeedAnEntryAddress()
    {
        var result = repo.Run("add", "billing", "--kind", "rule", "--summary", "Round");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--entry"));
        Assert.That(repo.Store.Exists("billing"), Is.False);
    }

    [Test]
    public void EntryFlagDerivesUniqueIdsFromTheSummary()
    {
        repo.Run("add", "billing");

        var first = Json("add", "billing", "--entry", "--kind", "rule", "--summary", "Round half to even!", "--json");
        var second = Json("add", "billing", "--entry", "--kind", "fact", "--summary", "Round half to even", "--json");

        Assert.That(first.GetProperty("address").GetString(), Is.EqualTo("billing/round-half-to-even"));
        Assert.That(second.GetProperty("address").GetString(), Is.EqualTo("billing/round-half-to-even-2"));
        Assert.That(repo.Store.LoadFeature("billing").Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "round-half-to-even", "round-half-to-even-2" }));
    }

    [Test]
    public void EntryFlagWithAnEntryAddressIsAUsageError()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rounding", "--entry", "--kind", "rule", "--summary", "Round");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void ExistingOrInvalidEntryIdsAreUsageErrors()
    {
        repo.Run("add", "billing");
        repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round");

        Assert.That(repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Again").Errors, Does.Contain("already exists"));
        Assert.That(repo.Run("add", "billing/Rounding_2", "--kind", "rule", "--summary", "Again").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [TestCase("missing/rounding")]
    [TestCase("missing")]
    public void AddingAnEntryToAMissingFeatureIsNotFound(string target)
    {
        var args = target.Contains('/') ? new[] { "add", target } : ["add", target, "--entry"];

        var result = repo.Run([.. args, "--kind", "rule", "--summary", "Round"]);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.NotFound));
    }

    [Test]
    public void EntryJsonHasAddressFileAndEntry()
    {
        repo.Run("add", "billing");

        var json = Json("add", "billing/rounding", "--kind", "rule", "--summary", "Round", "--anchor", "src/Money.cs:7", "--json");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("billing/rounding"));
        Assert.That(json.GetProperty("file").GetString(), Is.EqualTo(".repo-docs/billing.yaml"));
        Assert.That(json.TryGetProperty("feature", out _), Is.False);
        var entry = json.GetProperty("entry");
        Assert.That(entry.GetProperty("id").GetString(), Is.EqualTo("rounding"));
        Assert.That(entry.GetProperty("kind").GetString(), Is.EqualTo("rule"));
        Assert.That(entry.GetProperty("summary").GetString(), Is.EqualTo("Round"));
        Assert.That(entry.GetProperty("created").GetDateTimeOffset().Offset, Is.EqualTo(TimeSpan.Zero));
        Assert.That(entry.GetProperty("steps").GetArrayLength(), Is.Zero);
        var anchor = entry.GetProperty("when").GetProperty("anchors")[0];
        Assert.That(anchor.GetProperty("path").GetString(), Is.EqualTo("src/Money.cs"));
        Assert.That(anchor.GetProperty("line").GetInt32(), Is.EqualTo(7));
        Assert.That(anchor.GetProperty("symbol").GetString(), Is.EqualTo("Money.Round"));
    }

    [Test]
    public void PlanEntriesTakeStepsWithIssues()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rollout", "--kind", "plan", "--summary", "Roll out", "--step", "Schema and store #23 #24", "--step", "Write the guide");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var steps = repo.Store.LoadFeature("billing").Entries.Single().Steps;
        Assert.That(steps.Select(step => step.Title), Is.EqualTo(new[] { "Schema and store", "Write the guide" }));
        Assert.That(steps[0].Issues, Is.EqualTo(new[] { 23, 24 }));
        Assert.That(steps[1].Issues, Is.Empty);
    }

    [Test]
    public void StepsOnANonPlanEntryAreRejected()
    {
        repo.Run("add", "billing");

        var result = repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round", "--step", "Do it");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("only 'plan' entries may have steps"));
    }

    [Test]
    public void RepoEntriesGoIntoRepoYaml()
    {
        var result = repo.Run("add", "repo/style", "--kind", "rule", "--summary", "Use file-scoped namespaces");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Is.EqualTo("Created repo/style (.repo-docs/repo.yaml)\n"));
        Assert.That(repo.Store.Load().Repo.Entries.Single().Id, Is.EqualTo("style"));
    }

    [Test]
    public void BodyComesFromAFileOrStdin()
    {
        repo.Run("add", "billing");
        var bodyFile = Path.Combine(repo.Root, "body.md");
        File.WriteAllText(bodyFile, "From a file.\r\n\r\n");

        repo.Run("add", "billing/from-file", "--kind", "rule", "--summary", "File", "--body-file", bodyFile);
        var previous = Console.In;
        Console.SetIn(new StringReader("From stdin.\n"));
        try
        {
            var result = repo.Run("add", "billing/from-stdin", "--kind", "rule", "--summary", "Stdin", "--body-stdin");
            Assert.That(result.ExitCode, Is.Zero, result.Errors);
        }
        finally
        {
            Console.SetIn(previous);
        }

        var entries = repo.Store.LoadFeature("billing").Entries;
        Assert.That(entries.Select(entry => entry.Body), Is.EqualTo(new[] { "From a file.", "From stdin." }));
        Assert.That(repo.Run("add", "billing/missing", "--kind", "rule", "--summary", "X", "--body-file", "nope.md").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void AddNeedsAnInitializedFolder()
    {
        using var empty = new TempRepo();

        var result = empty.Run("add", "billing");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("docs init"));
    }

    JsonElement Json(params string[] args)
    {
        var result = repo.Run(args);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return JsonDocument.Parse(result.Output).RootElement;
    }
}
