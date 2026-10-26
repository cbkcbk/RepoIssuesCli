using System.Text.Json;
using RepoCli.Core.Features.Text;

namespace RepoDocsCli.Tests.Cli;

public class BudgetCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    string ClaudeMdPath => Path.Combine(repo.Root, "CLAUDE.md");

    JsonElement Budget(params string[] args)
    {
        var result = repo.Run(["budget", "--json", .. args]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return JsonDocument.Parse(result.Output).RootElement;
    }

    void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(repo.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    static Dictionary<string, JsonElement> ByName(JsonElement array, string key) =>
        array.EnumerateArray().ToDictionary(item => item.GetProperty(key).GetString()!);

    [Test]
    public void SplitsClaudeMdIntoTextOutsideTheBlockAndTheBlock()
    {
        QueryFixture.Write(repo);
        repo.Run("render");
        var block = File.ReadAllText(ClaudeMdPath).TrimEnd('\n');
        File.WriteAllText(ClaudeMdPath, "# Global rules\n\nBe brief.\n\n" + block + "\n");

        var claudeMd = Budget().GetProperty("alwaysLoaded").GetProperty("claudeMd");

        Assert.That(claudeMd.GetProperty("exists").GetBoolean(), Is.True);
        Assert.That(claudeMd.GetProperty("outsideBlock").GetInt32(), Is.EqualTo(TokenEstimate.Of("# Global rules\n\nBe brief.\n\n\n")));
        Assert.That(claudeMd.GetProperty("block").GetInt32(), Is.EqualTo(TokenEstimate.Of(block)));
        Assert.That(claudeMd.GetProperty("blockIfRendered").GetInt32(), Is.EqualTo(TokenEstimate.Of(block)));
        Assert.That(claudeMd.GetProperty("stale").GetBoolean(), Is.False);
    }

    [Test]
    public void AStaleBlockShowsWhatARenderWouldCost()
    {
        QueryFixture.Write(repo);
        repo.Run("render");
        repo.WriteDocsFile("payments.yaml", "feature: payments\nsummary: Card payments through the provider\n");

        var json = Budget();
        var claudeMd = json.GetProperty("alwaysLoaded").GetProperty("claudeMd");

        Assert.That(claudeMd.GetProperty("stale").GetBoolean(), Is.True);
        Assert.That(claudeMd.GetProperty("blockIfRendered").GetInt32(), Is.GreaterThan(claudeMd.GetProperty("block").GetInt32()));
        Assert.That(repo.Run("budget").Output, Does.Contain("Block if rendered now:").And.Contain("stale, +"));
    }

    [Test]
    public void ClaudeMdWithoutABlockCountsEverythingOutsideIt()
    {
        QueryFixture.Write(repo);
        File.WriteAllText(ClaudeMdPath, "# Rules\r\n\r\nBe brief.\r\n");

        var loaded = Budget().GetProperty("alwaysLoaded");
        var claudeMd = loaded.GetProperty("claudeMd");

        Assert.That(claudeMd.GetProperty("outsideBlock").GetInt32(), Is.EqualTo(TokenEstimate.Of("# Rules\n\nBe brief.\n")));
        Assert.That(claudeMd.GetProperty("block").GetInt32(), Is.Zero);
        Assert.That(claudeMd.GetProperty("blockIfRendered").GetInt32(), Is.GreaterThan(0));
        Assert.That(claudeMd.GetProperty("stale").GetBoolean(), Is.True);
        Assert.That(loaded.GetProperty("total").GetInt32(), Is.EqualTo(claudeMd.GetProperty("outsideBlock").GetInt32()));
    }

    [Test]
    public void ImportsAddToTheAlwaysLoadedTotal()
    {
        repo.Store.Initialize();
        WriteFile("docs/style.md", "Use tabs. @naming.md");
        WriteFile("docs/naming.md", "Name things well.");
        File.WriteAllText(ClaudeMdPath, "# Rules\n\n@docs/style.md\n@~/personal.md\n@gone.md\n");

        var loaded = Budget().GetProperty("alwaysLoaded");
        var imports = loaded.GetProperty("imports").EnumerateArray().ToList();

        Assert.That(imports.Select(import => (import.GetProperty("path").GetString(), import.GetProperty("depth").GetInt32(), import.GetProperty("from").GetString())),
            Is.EqualTo(new[] { ("docs/style.md", 1, "CLAUDE.md"), ("docs/naming.md", 2, "docs/style.md") }));
        Assert.That(loaded.GetProperty("total").GetInt32(), Is.EqualTo(
            TokenEstimate.Of("# Rules\n\n@docs/style.md\n@~/personal.md\n@gone.md\n") + TokenEstimate.Of("Use tabs. @naming.md") + TokenEstimate.Of("Name things well.")));
        Assert.That(loaded.GetProperty("external")[0].GetProperty("path").GetString(), Is.EqualTo("~/personal.md"));
        Assert.That(loaded.GetProperty("skipped")[0].GetProperty("path").GetString(), Is.EqualTo("gone.md"));
        Assert.That(loaded.GetProperty("skipped")[0].GetProperty("reason").GetString(), Is.EqualTo("missing"));
    }

    [Test]
    public void NestedClaudeMdFilesAreContextualNotAlwaysLoaded()
    {
        repo.Store.Initialize();
        File.WriteAllText(ClaudeMdPath, "Root rules.\n");
        WriteFile("Web/CLAUDE.md", "Web rules. @web-style.md\n");
        WriteFile("Web/web-style.md", "Use Tailwind classes for every component.");
        WriteFile("Api/Data/CLAUDE.md", "Data rules.\n");
        WriteFile(".hidden/CLAUDE.md", "Hidden.\n");

        var json = Budget();
        var contextual = json.GetProperty("contextual").EnumerateArray().ToList();

        Assert.That(contextual.Select(file => file.GetProperty("path").GetString()), Is.EqualTo(new[] { "Api/Data/CLAUDE.md", "Web/CLAUDE.md" }));
        Assert.That(contextual[1].GetProperty("tokens").GetInt32(), Is.EqualTo(TokenEstimate.Of("Web rules. @web-style.md\n")));
        Assert.That(contextual[1].GetProperty("importTokens").GetInt32(), Is.EqualTo(TokenEstimate.Of("Use Tailwind classes for every component.")));
        Assert.That(json.GetProperty("alwaysLoaded").GetProperty("total").GetInt32(), Is.EqualTo(TokenEstimate.Of("Root rules.\n")));
    }

    [Test]
    public void OnDemandGroupsEveryEntryByFeatureAndByKind()
    {
        QueryFixture.Write(repo);

        var onDemand = Budget().GetProperty("onDemand");
        var byFeature = ByName(onDemand.GetProperty("byFeature"), "feature");
        var byKind = ByName(onDemand.GetProperty("byKind"), "kind");

        Assert.That(onDemand.GetProperty("entries").GetInt32(), Is.EqualTo(8));
        Assert.That(byFeature.ToDictionary(pair => pair.Key, pair => pair.Value.GetProperty("entries").GetInt32()),
            Is.EquivalentTo(new Dictionary<string, int> { ["repo"] = 2, ["billing"] = 4, ["search"] = 2 }));
        Assert.That(byKind.ToDictionary(pair => pair.Key, pair => pair.Value.GetProperty("entries").GetInt32()),
            Is.EquivalentTo(new Dictionary<string, int> { ["rule"] = 2, ["decision"] = 2, ["plan"] = 2, ["gotcha"] = 1, ["fact"] = 1 }));

        var total = onDemand.GetProperty("total").GetInt32();
        Assert.That(byFeature.Values.Sum(cost => cost.GetProperty("tokens").GetInt32()), Is.EqualTo(total));
        Assert.That(byKind.Values.Sum(cost => cost.GetProperty("tokens").GetInt32()), Is.EqualTo(total));

        var billing = byFeature["billing"];
        Assert.That(billing.GetProperty("bodyTokens").GetInt32(), Is.EqualTo(TokenEstimate.Of("Use MidpointRounding.ToEven.\n")));
        Assert.That(billing.GetProperty("tokens").GetInt32(),
            Is.EqualTo(billing.GetProperty("summaryTokens").GetInt32() + billing.GetProperty("bodyTokens").GetInt32()));
        Assert.That(byKind["decision"].GetProperty("render").GetString(), Is.EqualTo("never"));
        Assert.That(byKind["rule"].GetProperty("push").GetString(), Is.EqualTo("on-edit"));
    }

    [Test]
    public void PushCountsShortBodiesInFullAndLongOnesAsSummaryOnly()
    {
        QueryFixture.Write(repo);
        var longBody = string.Join(" ", Enumerable.Repeat("word", 200));
        repo.WriteDocsFile("payments.yaml",
            $"feature: payments\nstatus: shipped\nentries:\n  - id: retries\n    kind: gotcha\n    summary: Retries are idempotent\n    body: {longBody}\n  - id: plan\n    kind: plan\n    summary: Not active\n");

        var byPolicy = ByName(Budget().GetProperty("push").GetProperty("byPolicy"), "policy");

        var onEdit = byPolicy["on-edit"];
        Assert.That(onEdit.GetProperty("entries").GetInt32(), Is.EqualTo(4));
        Assert.That(onEdit.GetProperty("full").GetInt32(), Is.EqualTo(3));
        Assert.That(onEdit.GetProperty("summaryOnly").GetInt32(), Is.EqualTo(1));
        Assert.That(onEdit.GetProperty("tokens").GetInt32(), Is.EqualTo(new[]
        {
            "Use file-scoped namespaces", "Round half to even", "Use MidpointRounding.ToEven.\n", "Money is a decimal", "Retries are idempotent",
        }.Sum(TokenEstimate.Of)));
        Assert.That(byPolicy["on-read"].GetProperty("entries").GetInt32(), Is.EqualTo(1));
        Assert.That(byPolicy["when-feature-active"].GetProperty("entries").GetInt32(), Is.EqualTo(1));
        Assert.That(byPolicy.Keys, Is.EquivalentTo(new[] { "on-edit", "on-read", "when-feature-active" }));
    }

    [Test]
    public void TopListsTheLargestEntries()
    {
        QueryFixture.Write(repo);

        var largest = Budget("--top", "2").GetProperty("largest").EnumerateArray().ToList();

        Assert.That(largest, Has.Count.EqualTo(2));
        Assert.That(largest[0].GetProperty("address").GetString(), Is.EqualTo("billing/rounding"));
        Assert.That(largest[0].GetProperty("kind").GetString(), Is.EqualTo("rule"));
        Assert.That(largest[0].GetProperty("tokens").GetInt32(), Is.GreaterThanOrEqualTo(largest[1].GetProperty("tokens").GetInt32()));
        Assert.That(Budget("--top", "0").GetProperty("largest").GetArrayLength(), Is.Zero);
        Assert.That(repo.Run("budget", "--top", "-1").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void JsonAlwaysHasEveryArray()
    {
        repo.Store.Initialize();

        var json = Budget();
        var loaded = json.GetProperty("alwaysLoaded");

        Assert.That(json.GetProperty("method").GetString(), Is.EqualTo(TokenEstimate.Method));
        Assert.That(loaded.GetProperty("total").GetInt32(), Is.Zero);
        Assert.That(loaded.GetProperty("claudeMd").GetProperty("path").GetString(), Is.EqualTo("CLAUDE.md"));
        Assert.That(loaded.GetProperty("claudeMd").GetProperty("exists").GetBoolean(), Is.False);
        foreach (var array in new[]
                 {
                     loaded.GetProperty("imports"), loaded.GetProperty("external"), loaded.GetProperty("skipped"), json.GetProperty("contextual"),
                     json.GetProperty("onDemand").GetProperty("byKind"), json.GetProperty("largest"), json.GetProperty("notes"),
                 })
            Assert.That(array.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(json.GetProperty("onDemand").GetProperty("byFeature")[0].GetProperty("feature").GetString(), Is.EqualTo("repo"));
        Assert.That(json.GetProperty("push").GetProperty("byPolicy").GetArrayLength(), Is.EqualTo(3));
        Assert.That(json.GetProperty("push").GetProperty("total").GetInt32(), Is.Zero);
    }

    [Test]
    public void WithoutRepoDocsItStillMeasuresClaudeMdAndItsImports()
    {
        WriteFile("rules.md", "Keep it short.");
        File.WriteAllText(ClaudeMdPath, "@rules.md\n");

        var result = repo.Run("budget", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;
        var loaded = json.GetProperty("alwaysLoaded");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(loaded.GetProperty("imports")[0].GetProperty("path").GetString(), Is.EqualTo("rules.md"));
        Assert.That(loaded.GetProperty("claudeMd").TryGetProperty("blockIfRendered", out _), Is.False);
        Assert.That(json.GetProperty("onDemand").GetProperty("byFeature").GetArrayLength(), Is.Zero);
        Assert.That(json.GetProperty("notes")[0].GetString(), Does.Contain(".repo-docs"));
        Assert.That(repo.Store.IsInitialized, Is.False);
        Assert.That(repo.Run("budget").Output, Does.Contain("Note:").And.Contain("Always loaded:"));
    }

    [Test]
    public void TextShowsTablesWithTotalsAndByChoosesTheGrouping()
    {
        QueryFixture.Write(repo);
        File.WriteAllText(ClaudeMdPath, "Rules.\n");

        var both = repo.Run("budget");
        var byKind = repo.Run("budget", "--by", "kind");

        Assert.That(both.ExitCode, Is.Zero, both.Errors);
        Assert.That(both.Output, Does.Contain("Token estimate:").And.Contain("Always loaded:").And.Contain("On demand:")
            .And.Contain("Push:").And.Contain("Largest entries").And.Contain("Total").And.Contain("Feature").And.Contain("Kind"));
        Assert.That(byKind.Output, Does.Contain("Kind").And.Not.Contain("│ Feature"));
        Assert.That(File.ReadAllText(ClaudeMdPath), Is.EqualTo("Rules.\n"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(QueryFixture.Billing + "\n"));
    }
}
