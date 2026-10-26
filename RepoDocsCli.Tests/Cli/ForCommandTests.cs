using System.Text.Json;

namespace RepoDocsCli.Tests.Cli;

public class ForCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        QueryFixture.Write(repo);
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    JsonElement Json(params string[] args)
    {
        var result = repo.Run(["for", .. args, "--json"]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return JsonDocument.Parse(result.Output).RootElement;
    }

    static string[] Addresses(JsonElement json) =>
        json.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("address").GetString()!).ToArray();

    static string[] Features(JsonElement json) =>
        json.GetProperty("features").EnumerateArray().Select(feature => feature.GetProperty("feature").GetString()!).ToArray();

    [Test]
    public void PathUnderAFeatureFolderGetsRepoEntriesAndInheritingEntries()
    {
        var json = Json("App/Features/Billing/Invoice.cs");

        Assert.That(json.GetProperty("path").GetString(), Is.EqualTo("App/Features/Billing/Invoice.cs"));
        Assert.That(Addresses(json), Is.EqualTo(new[] { "repo/style", "billing/rounding", "billing/rollout" }));
        Assert.That(Features(json), Is.EqualTo(new[] { "billing" }));
        var entries = json.GetProperty("entries");
        Assert.That(entries[0].GetProperty("match").GetString(), Is.EqualTo("repo"));
        Assert.That(entries[1].GetProperty("match").GetString(), Is.EqualTo("feature"));
        Assert.That(entries[1].GetProperty("body").GetString(), Is.EqualTo("Use MidpointRounding.ToEven.\n"));
    }

    [TestCase("App/Features/Billing")]
    [TestCase("App/Features/Billing/")]
    [TestCase("./App/Features/Billing/Sub/Deep.cs")]
    [TestCase("app/features/billing/Invoice.cs")]
    public void FolderPrefixMatchesOnFolderBoundaries(string path)
    {
        Assert.That(Features(Json(path)), Is.EqualTo(new[] { "billing" }));
    }

    [TestCase("App/Features/BillingOld/Invoice.cs")]
    [TestCase("App/Features")]
    public void SimilarPrefixDoesNotMatch(string path)
    {
        Assert.That(Features(Json(path)), Is.Empty);
    }

    [Test]
    public void EntryWithItsOwnPathsMatchesOnThoseOnly()
    {
        var json = Json("App/Shared/Money.cs");

        Assert.That(Addresses(json), Is.EqualTo(new[] { "repo/style", "billing/money" }));
        Assert.That(json.GetProperty("entries")[1].GetProperty("match").GetString(), Is.EqualTo("entry"));
        Assert.That(Features(json), Is.EqualTo(new[] { "billing" }));
    }

    [Test]
    public void AnchorOnTheFileMatches()
    {
        Assert.That(Addresses(Json("App/Search/Index.cs")), Is.EqualTo(new[] { "repo/style", "search/ranking" }));
        Assert.That(Features(Json("App/Search/Other.cs")), Is.Empty);
    }

    [Test]
    public void PlansNeedAnInProgressFeatureOrThePlansFlag()
    {
        Assert.That(Addresses(Json("App/Search/Index.cs", "--plans")), Is.EqualTo(new[] { "repo/style", "search/ranking", "search/launch" }));
        Assert.That(Addresses(Json("App/Features/Billing/Invoice.cs")), Does.Contain("billing/rollout"));
    }

    [Test]
    public void AllKindsIncludesDecisions()
    {
        Assert.That(Addresses(Json("App/Features/Billing/Invoice.cs", "--all-kinds")),
            Is.EqualTo(new[] { "repo/style", "repo/history", "billing/rounding", "billing/rollout", "billing/why-yaml" }));
    }

    [Test]
    public void PathOutsideEveryFeatureGetsOnlyRepoEntries()
    {
        var json = Json("README.md");

        Assert.That(Addresses(json), Is.EqualTo(new[] { "repo/style" }));
        Assert.That(json.GetProperty("features").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void EmptyResultStillHasArrays()
    {
        repo.WriteDocsFile("repo.yaml", "entries: []\n");

        var json = Json("README.md");

        Assert.That(json.GetProperty("entries").GetArrayLength(), Is.Zero);
        Assert.That(json.GetProperty("features").GetArrayLength(), Is.Zero);
        Assert.That(repo.Run("for", "README.md").Output, Does.Contain("No docs apply to README.md."));
    }

    [Test]
    public void AbsolutePathsAreMadeRepoRelative()
    {
        var path = Path.Combine(repo.Root, "App", "Features", "Billing", "Invoice.cs");

        Assert.That(Json(path).GetProperty("path").GetString(), Is.EqualTo("App/Features/Billing/Invoice.cs"));
    }

    [Test]
    public void RelativePathsResolveAgainstTheCurrentDirectoryInsideTheRepo()
    {
        var folder = repo.Subfolder("App", "Features", "Billing");

        var result = repo.RunIn(folder, "for", "Invoice.cs", "--json");

        Assert.That(JsonDocument.Parse(result.Output).RootElement.GetProperty("path").GetString(), Is.EqualTo("App/Features/Billing/Invoice.cs"));
    }

    [Test]
    public void PathOutsideTheRepositoryIsAUsageError()
    {
        var result = repo.Run("for", Path.Combine(Path.GetTempPath(), "elsewhere.cs"));

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("outside the repository root"));
    }

    [Test]
    public void LongBodiesNeedFull()
    {
        var body = string.Concat(Enumerable.Repeat("Long line of text.\n", 50));
        repo.WriteDocsFile("notes.yaml",
            "feature: notes\nwhen:\n  paths:\n    - Notes/\nentries:\n  - id: long\n    kind: fact\n    summary: Long one\n    body: |\n"
            + string.Concat(body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"      {line}\n")));

        var brief = Json("Notes/a.md").GetProperty("entries")[1];
        var full = Json("Notes/a.md", "--full").GetProperty("entries")[1];

        Assert.That(brief.GetProperty("hasBody").GetBoolean(), Is.True);
        Assert.That(brief.TryGetProperty("body", out _), Is.False);
        Assert.That(full.GetProperty("body").GetString(), Is.EqualTo(body));
    }

    [Test]
    public void ForDoesNotRecordReads()
    {
        Json("App/Features/Billing/Invoice.cs", "--full");

        Assert.That(repo.Store.LoadFeature("billing").Entries.Select(entry => entry.Read), Is.All.Null);
    }

    [Test]
    public void TextGroupsEntriesByFeature()
    {
        var result = repo.Run("for", "App/Features/Billing/Invoice.cs");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("App/Features/Billing/Invoice.cs"));
        Assert.That(result.Output, Does.Contain("repo/style rule Use file-scoped namespaces"));
        Assert.That(result.Output, Does.Contain("billing in-progress Invoices and payments"));
        Assert.That(result.Output, Does.Contain("Use MidpointRounding.ToEven."));
    }
}
