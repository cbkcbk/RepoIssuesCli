using System.Text.Json;
using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Cli;

public class AffectedCommandTests
{
    const string Calculator = """
        namespace Demo;

        public class Calculator
        {
            public int Add(int a, int b)
            {
                var sum = a + b;
                return sum;
            }

            public int Negate(int a) => -a;
        }

        """;

    const string CalculatorPath = "src/Calculator.cs";
    const int AddLine = 5;
    const int AddEndLine = 9;

    static readonly DateTimeOffset Stamp = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.InitGit();
        repo.Store.Initialize();
        WriteSource(CalculatorPath, Calculator);
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    void WriteSource(string path, string content)
    {
        var full = Path.Combine(repo.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void Commit(string message = "commit")
    {
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", message);
    }

    static FeatureEntry Rule(WhenHints? when = null, string id = "rounding") =>
        new() { Id = id, Kind = "rule", Summary = "Round half to even", Body = "Use ToEven.", When = when, Created = Stamp, Updated = Stamp };

    static Feature Billing(WhenHints? when = null, params FeatureEntry[] entries) =>
        new() { Id = "billing", Status = FeatureStatus.Shipped, Summary = "Invoices", When = when ?? new WhenHints(), Created = Stamp, Updated = Stamp, Entries = [.. entries] };

    static WhenHints Anchored(Anchor anchor) => new() { Anchors = [anchor] };

    Anchor AddAnchor() => Anchor.Load(repo.Root, CalculatorPath, AddLine, AddEndLine);

    void Prepare(Feature feature)
    {
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(feature));
        Commit();
    }

    void EditAddBody() => WriteSource(CalculatorPath, Calculator.Replace("var sum = a + b;", "var sum = checked(a + b);"));

    JsonElement Affected(params string[] args)
    {
        var result = repo.Run(["affected", "--json", .. args]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return JsonDocument.Parse(result.Output).RootElement;
    }

    static List<string?> Addresses(JsonElement result) =>
        [.. result.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("address").GetString())];

    static JsonElement OnlyMatch(JsonElement result, string address) =>
        result.GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("address").GetString() == address)
            .GetProperty("matched").EnumerateArray().Single();

    [Test]
    public void EditedAnchoredLineWithoutAnEntryUpdateIsAffected()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        EditAddBody();

        var result = Affected();

        Assert.That(Addresses(result), Is.EqualTo(new[] { "billing/rounding" }));
        var match = OnlyMatch(result, "billing/rounding");
        Assert.That(match.GetProperty("path").GetString(), Is.EqualTo(CalculatorPath));
        Assert.That(match.GetProperty("anchor").GetString(), Is.EqualTo($"{CalculatorPath}:{AddLine}"));
        Assert.That(match.GetProperty("reason").GetString(), Is.EqualTo("AnchorEdited"));
    }

    [Test]
    public void EditingTheEntryYamlInTheSameDiffCountsAsUpdated()
    {
        var anchor = AddAnchor();
        Prepare(Billing(entries: Rule(Anchored(anchor))));
        EditAddBody();
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(Billing(entries: Rule(Anchored(anchor)) with { Body = "Use checked arithmetic." })));

        Assert.That(Addresses(Affected()), Is.Empty);
    }

    [Test]
    public void EditOutsideTheAnchorSpanDoesNotAffectIt()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        WriteSource(CalculatorPath, Calculator.Replace("=> -a;", "=> 0 - a;"));

        Assert.That(Addresses(Affected()), Is.Empty);
    }

    [Test]
    public void AnchorSpanIsResolvedInTheOldVersion()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        WriteSource(CalculatorPath, "// header\n// more\n" + Calculator.Replace("=> -a;", "=> 0 - a;"));

        Assert.That(Addresses(Affected()), Is.Empty);
    }

    [Test]
    public void PathHintHitAffectsTheFeatureAndEntriesThatInheritIt()
    {
        Prepare(Billing(new WhenHints { Paths = ["src/"] },
            Rule(),
            Rule(new WhenHints { Paths = ["lib/"] }, id: "elsewhere")));
        WriteSource(CalculatorPath, Calculator.Replace("=> -a;", "=> 0 - a;"));

        var result = Affected();

        Assert.That(Addresses(result), Is.EqualTo(new[] { "billing", "billing/rounding" }));
        var match = OnlyMatch(result, "billing/rounding");
        Assert.That(match.GetProperty("reason").GetString(), Is.EqualTo("PathModified"));
        Assert.That(match.TryGetProperty("anchor", out _), Is.False);
        Assert.That(result.GetProperty("entries")[0].GetProperty("kind").GetString(), Is.EqualTo("feature"));
    }

    [Test]
    public void AnchorHitsSortBeforePathHits()
    {
        Prepare(Billing(new WhenHints { Paths = ["src/"] }, Rule(Anchored(AddAnchor()), id: "anchored")));
        EditAddBody();

        Assert.That(Addresses(Affected()), Is.EqualTo(new[] { "billing/anchored", "billing" }));
    }

    [Test]
    public void DeletedAnchoredFileIsAffected()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        File.Delete(Path.Combine(repo.Root, CalculatorPath));

        Assert.That(OnlyMatch(Affected(), "billing/rounding").GetProperty("reason").GetString(), Is.EqualTo("AnchorDeleted"));
    }

    [Test]
    public void RenamedAnchoredFileIsAffected()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        repo.Git("mv", CalculatorPath, "src/Arithmetic.cs");

        var match = OnlyMatch(Affected(), "billing/rounding");

        Assert.That(match.GetProperty("reason").GetString(), Is.EqualTo("AnchorRenamed"));
        Assert.That(match.GetProperty("path").GetString(), Is.EqualTo("src/Arithmetic.cs"));
        Assert.That(match.GetProperty("previousPath").GetString(), Is.EqualTo(CalculatorPath));
    }

    [Test]
    public void TouchClearsTheFlagAfterACommit()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        EditAddBody();
        Commit("edit add");

        Assert.That(Addresses(Affected("--since", "HEAD~1")), Is.EqualTo(new[] { "billing/rounding" }));

        Assert.That(repo.Run("touch", "billing/rounding").ExitCode, Is.Zero);
        Assert.That(Addresses(Affected("--since", "HEAD~1")), Is.Empty);

        Commit("touch");
        Assert.That(Addresses(Affected("--since", "HEAD~2")), Is.Empty);
    }

    [Test]
    public void TouchClearsTheFlagForUncommittedEdits()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        EditAddBody();
        File.SetLastWriteTimeUtc(Path.Combine(repo.Root, CalculatorPath), DateTime.UtcNow.AddMinutes(-1));

        Assert.That(repo.Run("touch", "billing/rounding").ExitCode, Is.Zero);

        Assert.That(Addresses(Affected()), Is.Empty);
    }

    [Test]
    public void SinceAnOlderCommitIncludesCommittedChanges()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        EditAddBody();
        Commit("edit add");
        WriteSource("README.md", "readme\n");
        Commit("unrelated");

        Assert.That(Addresses(Affected()), Is.Empty);
        Assert.That(Addresses(Affected("--since", "HEAD~1")), Is.Empty);
        Assert.That(Addresses(Affected("--since", "HEAD~2")), Is.EqualTo(new[] { "billing/rounding" }));
    }

    [Test]
    public void UntrackedFilesUnderAPathHintAreAffecting()
    {
        Prepare(Billing(new WhenHints { Paths = ["src/"] }));
        WriteSource("src/Money.cs", "class Money;\n");

        var match = OnlyMatch(Affected(), "billing");

        Assert.That(match.GetProperty("path").GetString(), Is.EqualTo("src/Money.cs"));
        Assert.That(match.GetProperty("reason").GetString(), Is.EqualTo("PathAdded"));
    }

    [Test]
    public void NewEntriesAreNotAffected()
    {
        Prepare(Billing(new WhenHints { Paths = ["src/"] }));
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(Billing(new WhenHints { Paths = ["src/"] }, Rule(id: "fresh"))));
        EditAddBody();

        Assert.That(Addresses(Affected()), Is.EqualTo(new[] { "billing" }));
    }

    [Test]
    public void DecisionsAreLeftOut()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor())) with { Kind = "decision" }));
        EditAddBody();

        Assert.That(Addresses(Affected()), Is.Empty);
    }

    [Test]
    public void StrictExitsOneWhenAnythingIsAffected()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));

        Assert.That(repo.Run("affected", "--strict").ExitCode, Is.Zero);

        EditAddBody();
        var result = repo.Run("affected", "--strict");
        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(repo.Run("affected").ExitCode, Is.Zero);
        Assert.That(result.Output, Does.Contain("billing/rounding").And.Contain("AnchorEdited").And.Contain("docs touch <address>"));
    }

    [Test]
    public void JsonShape()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        EditAddBody();

        var result = Affected();

        Assert.That(result.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "since", "entries" }));
        Assert.That(result.GetProperty("since").GetString(), Is.EqualTo("HEAD"));
        var entry = result.GetProperty("entries").EnumerateArray().Single();
        Assert.That(entry.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "address", "kind", "summary", "matched", "suggestion" }));
        Assert.That(entry.GetProperty("kind").GetString(), Is.EqualTo("rule"));
        Assert.That(entry.GetProperty("summary").GetString(), Is.EqualTo("Round half to even"));
        Assert.That(entry.GetProperty("suggestion").GetString(), Does.Contain("docs edit billing/rounding").And.Contain("docs touch billing/rounding"));
        Assert.That(entry.GetProperty("matched")[0].EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "path", "anchor", "reason" }));
    }

    [Test]
    public void CleanTreePrintsNothingAffected()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));

        var result = repo.Run("affected");

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Output, Is.EqualTo("No docs affected since HEAD.\n"));
        Assert.That(Affected().GetProperty("entries").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void UnknownRefIsAUsageError()
    {
        Prepare(Billing());

        var result = repo.Run("affected", "--since", "no-such-branch");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("no-such-branch"));
    }

    [Test]
    public void WithoutRepoDocsExitsTwo()
    {
        Directory.Delete(repo.Folder, recursive: true);

        var result = repo.Run("affected");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("docs init"));
    }
}
