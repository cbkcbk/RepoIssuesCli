using System.Text.Json;

namespace RepoDocsCli.Tests.Cli;

public class FindAndListCommandTests
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
        var result = repo.Run([.. args, "--json"]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return JsonDocument.Parse(result.Output).RootElement;
    }

    static (string, int)[] Hits(JsonElement json) =>
        json.EnumerateArray().Select(hit => (hit.GetProperty("address").GetString()!, hit.GetProperty("score").GetInt32())).ToArray();

    [Test]
    public void FindRanksIdThenSummaryThenKeywordsThenBody()
    {
        repo.WriteDocsFile("notes.yaml",
            "feature: notes\nentries:\n  - id: rounding\n    kind: fact\n    summary: Unrelated\n  - id: tips\n    kind: howto\n    summary: Tips\n    body: |\n      Rounding is hard.\n  - id: kw\n    kind: fact\n    summary: Other\n    when:\n      keywords:\n        - rounding\n");

        var hits = Hits(Json("find", "rounding"));

        Assert.That(hits, Is.EqualTo(new[]
        {
            ("billing/rounding", 1001),
            ("notes/rounding", 1000),
            ("notes/kw", 10),
            ("notes/tips", 1),
        }));
    }

    [Test]
    public void FindCountsEachTermAndTiesBreakByAddress()
    {
        var hits = Hits(Json("find", "invoices YAML"));

        Assert.That(hits, Is.EqualTo(new[]
        {
            ("billing/why-yaml", 200),
            ("billing", 100),
            ("billing/rollout", 100),
            ("repo/history", 100),
        }));
    }

    [Test]
    public void FindMatchesFeaturesByKeyword()
    {
        var hit = Json("find", "invoice")[0];

        Assert.That(hit.GetProperty("address").GetString(), Is.EqualTo("billing"));
        Assert.That(hit.GetProperty("kind").GetString(), Is.EqualTo("feature"));
        Assert.That(hit.GetProperty("feature").GetString(), Is.EqualTo("billing"));
        Assert.That(hit.GetProperty("summary").GetString(), Is.EqualTo("Invoices and payments"));
        Assert.That(hit.GetProperty("score").GetInt32(), Is.EqualTo(110));
    }

    [Test]
    public void FindHonoursTheLimit()
    {
        Assert.That(Json("find", "invoices YAML", "--limit", "2").GetArrayLength(), Is.EqualTo(2));
        Assert.That(repo.Run("find", "x", "--limit", "0").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void FindWithoutHitsIsAnEmptyArray()
    {
        Assert.That(Json("find", "zebra").GetArrayLength(), Is.Zero);
        Assert.That(repo.Run("find", "zebra").Output, Does.Contain("No matches."));
    }

    [Test]
    public void FindDoesNotRecordReads()
    {
        Json("find", "rounding");

        Assert.That(repo.Store.LoadFeature("billing").Entries.Select(entry => entry.Read), Is.All.Null);
    }

    [Test]
    public void ListShowsFeaturesWithStatusSummaryAndEntryCount()
    {
        var json = Json("list");

        Assert.That(json.EnumerateArray().Select(item => (
                item.GetProperty("feature").GetString(),
                item.GetProperty("status").GetString(),
                item.GetProperty("summary").GetString(),
                item.GetProperty("entryCount").GetInt32())),
            Is.EqualTo(new[] { ("billing", "in-progress", "Invoices and payments", 4), ("search", "shipped", "Full-text search", 2) }));

        var text = repo.Run("list").Output;
        Assert.That(text, Does.Contain("billing").And.Contain("in-progress").And.Contain("search").And.Contain("shipped"));
    }

    [TestCase("in-progress", "billing")]
    [TestCase("Shipped", "search")]
    public void ListFiltersByStatus(string status, string feature)
    {
        var json = Json("list", "--status", status);

        Assert.That(json.EnumerateArray().Select(item => item.GetProperty("feature").GetString()), Is.EqualTo(new[] { feature }));
    }

    [Test]
    public void ListWithNoMatchesIsAnEmptyArray()
    {
        Assert.That(Json("list", "--status", "deprecated").GetArrayLength(), Is.Zero);
        Assert.That(repo.Run("list", "--status", "bogus").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void QueriesNeedRepoDocs()
    {
        using var empty = new TempRepo();

        string[][] commands = [["list"], ["find", "x"], ["for", "a.cs"], ["show", "billing"]];
        foreach (var args in commands)
        {
            var result = empty.Run(args);
            Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage), string.Join(' ', args));
            Assert.That(result.Errors, Does.Contain("docs init"));
        }
    }

    [Test]
    public void BrokenFilesAreWarnedAboutOnStderr()
    {
        repo.WriteDocsFile("broken.yaml", "feature: [unclosed");

        var result = repo.Run("list", "--json");

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Errors, Does.Contain("Warning:").And.Contain("broken.yaml"));
        Assert.That(JsonDocument.Parse(result.Output).RootElement.GetArrayLength(), Is.EqualTo(2));
    }
}
