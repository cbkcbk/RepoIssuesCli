using System.Text.Json;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Cli;

public class ShowCommandTests
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

    static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    JsonElement Json(params string[] args)
    {
        var result = repo.Run([.. args, "--json"]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return JsonDocument.Parse(result.Output).RootElement;
    }

    static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Test]
    public void FeatureShowsStatusHintsAndEntrySummaries()
    {
        var result = repo.Run("show", "billing");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("─billing─").And.Contain("in-progress").And.Contain("Invoices and payments"));
        Assert.That(result.Output, Does.Contain("App/Features/Billing/").And.Contain("invoice"));
        Assert.That(result.Output, Does.Contain("billing/rounding rule Round half to even (+ body)"));
        Assert.That(result.Output, Does.Not.Contain("MidpointRounding"));
    }

    [Test]
    public void FeatureJsonHasEntriesWithAddressesAndNoBodiesByDefault()
    {
        var json = Json("show", "billing");

        Assert.That(json.GetProperty("feature").GetString(), Is.EqualTo("billing"));
        Assert.That(json.GetProperty("status").GetString(), Is.EqualTo("in-progress"));
        Assert.That(Strings(json.GetProperty("when").GetProperty("paths")), Is.EqualTo(new[] { "App/Features/Billing/" }));
        Assert.That(json.GetProperty("when").GetProperty("anchors").GetArrayLength(), Is.Zero);
        var entries = json.GetProperty("entries");
        Assert.That(entries.EnumerateArray().Select(entry => entry.GetProperty("address").GetString()),
            Is.EqualTo(new[] { "billing/rounding", "billing/money", "billing/rollout", "billing/why-yaml" }));
        var rounding = entries[0];
        Assert.That(rounding.GetProperty("hasBody").GetBoolean(), Is.True);
        Assert.That(rounding.TryGetProperty("body", out _), Is.False);
        Assert.That(rounding.GetProperty("steps").GetArrayLength(), Is.Zero);
        Assert.That(rounding.TryGetProperty("match", out _), Is.False);
    }

    [Test]
    public void FeatureWithFullIncludesBodiesAndRecordsReads()
    {
        var json = Json("show", "billing", "--full");

        Assert.That(json.GetProperty("entries")[0].GetProperty("body").GetString(), Is.EqualTo("Use MidpointRounding.ToEven.\n"));
        Assert.That(json.GetProperty("entries")[0].GetProperty("read").GetString(), Is.EqualTo(Today.ToString(DocsTime.DateFormat)));
        Assert.That(repo.Store.LoadFeature("billing").Entries.Select(entry => entry.Read), Is.All.EqualTo(Today));
    }

    [Test]
    public void FeatureWithoutFullRecordsNoReads()
    {
        repo.Run("show", "billing");

        Assert.That(repo.Store.LoadFeature("billing").Entries.Select(entry => entry.Read), Is.All.Null);
    }

    [Test]
    public void EntryShowsItsFullBodyAndRecordsTheRead()
    {
        var result = repo.Run("show", "billing/rounding");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("─billing/rounding─").And.Contain("Round half to even").And.Contain("Use MidpointRounding.ToEven."));
        var entries = repo.Store.LoadFeature("billing").Entries;
        Assert.That(entries.Single(entry => entry.Id == "rounding").Read, Is.EqualTo(Today));
        Assert.That(entries.Where(entry => entry.Id != "rounding").Select(entry => entry.Read), Is.All.Null);
    }

    [Test]
    public void ReadIsWrittenOnlyWhenTheDateChanges()
    {
        repo.Run("show", "billing/rounding");
        var path = Path.Combine(repo.Folder, "billing.yaml");
        var written = File.ReadAllText(path);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        repo.Run("show", "billing/rounding");

        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(File.ReadAllText(path), Is.EqualTo(written));
    }

    [Test]
    public void EntryJsonShape()
    {
        var json = Json("show", "billing/money");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("billing/money"));
        Assert.That(json.GetProperty("feature").GetString(), Is.EqualTo("billing"));
        Assert.That(json.GetProperty("id").GetString(), Is.EqualTo("money"));
        Assert.That(json.GetProperty("kind").GetString(), Is.EqualTo("gotcha"));
        Assert.That(json.GetProperty("summary").GetString(), Is.EqualTo("Money is a decimal"));
        Assert.That(Strings(json.GetProperty("when").GetProperty("paths")), Is.EqualTo(new[] { "App/Shared/Money.cs" }));
        Assert.That(json.GetProperty("hasBody").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("read").GetString(), Is.EqualTo(Today.ToString(DocsTime.DateFormat)));
    }

    [Test]
    public void RepoEntryIsAddressedWithRepo()
    {
        var json = Json("show", "repo/style");

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("repo/style"));
        Assert.That(json.GetProperty("feature").GetString(), Is.EqualTo("repo"));
        Assert.That(repo.Store.Load().Repo.Entries.Single(entry => entry.Id == "style").Read, Is.EqualTo(Today));
    }

    [Test]
    public void RepoShowsTheRepoEntries()
    {
        var json = Json("show", "repo");

        Assert.That(json.GetProperty("feature").GetString(), Is.EqualTo("repo"));
        Assert.That(json.TryGetProperty("status", out _), Is.False);
        Assert.That(json.GetProperty("summary").GetString(), Is.EqualTo("Monorepo for the shop"));
        Assert.That(json.GetProperty("entries").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public void PlanIncludesProgressFromRepoIssues()
    {
        QueryFixture.WriteIssues(repo);

        var json = Json("show", "billing/rollout");
        var text = repo.Run("show", "billing/rollout");

        var progress = json.GetProperty("progress");
        Assert.That((progress.GetProperty("done").GetInt32(), progress.GetProperty("total").GetInt32()), Is.EqualTo((1, 2)));
        Assert.That(progress.GetProperty("steps")[0].GetProperty("state").GetString(), Is.EqualTo("in-progress"));
        Assert.That(json.GetProperty("steps")[0].GetProperty("title").GetString(), Is.EqualTo("Schema"));
        Assert.That(text.Output, Does.Contain("billing/rollout Roll out invoices 1/2").And.Contain("0002 Store InProgress"));
        Assert.That(text.Errors, Is.Empty);
    }

    [Test]
    public void FeaturePlanEntriesCarryProgressToo()
    {
        QueryFixture.WriteIssues(repo);

        var rollout = Json("show", "billing").GetProperty("entries")[2];

        Assert.That(rollout.GetProperty("progress").GetProperty("done").GetInt32(), Is.EqualTo(1));
        Assert.That(Json("show", "billing").GetProperty("entries")[0].TryGetProperty("progress", out _), Is.False);
    }

    [Test]
    public void PlanWithoutRepoIssuesSaysProgressIsUnavailable()
    {
        var json = repo.Run("show", "billing/rollout", "--json");
        var text = repo.Run("show", "billing/rollout");

        Assert.That(JsonDocument.Parse(json.Output).RootElement.TryGetProperty("progress", out _), Is.False);
        Assert.That(json.Errors, Does.Contain("No .repo-issues folder"));
        Assert.That(text.Output, Does.Contain("progress unavailable").And.Contain("Schema 0001, 0002"));
        Assert.That(text.Output, Does.Not.Contain("missing"));
    }

    [TestCase("missing", "Feature 'missing' not found.")]
    [TestCase("billing/missing", "Entry 'billing/missing' not found.")]
    [TestCase("repo/missing", "Entry 'repo/missing' not found.")]
    [TestCase("missing/rounding", "Feature 'missing' not found.")]
    public void MissingFeatureOrEntryExitsWithNotFound(string address, string message)
    {
        var result = repo.Run("show", address);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.NotFound));
        Assert.That(result.Errors, Does.Contain(message));
        Assert.That(result.Output, Is.Empty);
    }

    [TestCase("billing/rounding/extra")]
    [TestCase("Billing")]
    [TestCase("billing/")]
    [TestCase("config")]
    public void BadAddressIsAUsageError(string address)
    {
        var result = repo.Run("show", address);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("not a valid address"));
    }

    [Test]
    public void MissingAddressIsAUsageError()
    {
        Assert.That(repo.Run("show").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void BrokenFeatureFileIsAValidationError()
    {
        repo.WriteDocsFile("broken.yaml", "feature: [unclosed");

        var result = repo.Run("show", "broken");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(result.Errors, Does.Contain("broken.yaml"));
    }
}
