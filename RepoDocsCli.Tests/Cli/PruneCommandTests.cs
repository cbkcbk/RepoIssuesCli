using System.Text.Json;
using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Prune;

namespace RepoDocsCli.Tests.Cli;

public class PruneCommandTests
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
        }

        """;

    const string CalculatorPath = "src/Calculator.cs";
    const string Today = "2026-10-26";

    static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.InitGit();
        repo.Store.Initialize();
        WriteSource(CalculatorPath, Calculator);
        WriteSource("docs/notes.md", "# Notes\n");
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    void WriteSource(string path, string content)
    {
        var full = Path.Combine(repo.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    void Prepare(params Feature[] features)
    {
        foreach (var feature in features)
            repo.WriteDocsFile(DocsStore.FileNameFor(feature.Id), DocsYaml.Serialize(feature));
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "commit");
    }

    static FeatureEntry Entry(string id, WhenHints? when = null, string kind = "rule") =>
        new() { Id = id, Kind = kind, Summary = $"Summary of {id}", When = when, Created = Stamp, Updated = Stamp };

    static FeatureEntry Fact(string id, DateOnly? read, DateTimeOffset? created, DateTimeOffset? updated = null) =>
        new() { Id = id, Kind = "fact", Summary = "A fact", Read = read, Created = created, Updated = updated };

    static FeatureEntry Plan(params PlanStep[] steps) =>
        new() { Id = "rollout", Kind = PlanStep.EntryKind, Summary = "Roll out", Created = Stamp, Updated = Stamp, Steps = [.. steps] };

    static Feature Feature(string id, FeatureStatus status = FeatureStatus.Shipped, WhenHints? when = null, params FeatureEntry[] entries) =>
        new() { Id = id, Status = status, Summary = $"The {id} feature", When = when ?? new WhenHints(), Created = Stamp, Updated = Stamp, Entries = [.. entries] };

    static WhenHints Paths(params string[] paths) => new() { Paths = [.. paths] };

    Anchor AddAnchor() => Anchor.Load(repo.Root, CalculatorPath, 5, 9);

    void WriteIssue(int number, string status) =>
        File.WriteAllText(Path.Combine(repo.Subfolder(".repo-issues"), $"{number:D5}-issue.json"),
            $$"""{ "number": {{number}}, "name": "Issue {{number}}", "status": "{{status}}" }""");

    List<JsonElement> Candidates(params string[] args)
    {
        var result = repo.Run(["prune", "--json", "--today", Today, .. args]);
        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        return [.. JsonDocument.Parse(result.Output).RootElement.EnumerateArray()];
    }

    JsonElement Single(params string[] args) => Candidates(args).Single();

    static string?[] Codes(JsonElement candidate) =>
        [.. candidate.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetProperty("code").GetString())];

    static string? Action(JsonElement candidate) => candidate.GetProperty("suggestion").GetProperty("action").GetString();

    static string? Command(JsonElement candidate) => candidate.GetProperty("suggestion").GetProperty("command").GetString();

    static string? Address(JsonElement candidate) => candidate.GetProperty("address").GetString();

    [Test]
    public void CleanDocsHaveNoCandidates()
    {
        Prepare(Feature("billing", when: Paths("src/", "**/*.cs"), entries: Entry("rounding", new WhenHints { Anchors = [AddAnchor()], Keywords = ["vat"] })));

        var result = repo.Run("prune", "--today", Today);

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Output, Is.EqualTo("Nothing to prune.\n"));
        Assert.That(Candidates(), Is.Empty);
    }

    [Test]
    public void BrokenAnchorSuggestsReAnchoring()
    {
        var anchor = AddAnchor();
        Prepare(Feature("billing", entries: Entry("rounding", new WhenHints { Paths = ["docs/"], Anchors = [anchor] })));
        WriteSource(CalculatorPath, "namespace Demo;\n");

        var candidate = Single();

        Assert.That(Address(candidate), Is.EqualTo("billing/rounding"));
        Assert.That(Codes(candidate), Is.EqualTo(new[] { "BrokenAnchor" }));
        Assert.That(Action(candidate), Is.EqualTo("re-anchor"));
        Assert.That(Command(candidate), Is.EqualTo("docs edit billing/rounding ..."));
    }

    [Test]
    public void ChangedAnchorSuggestsReview()
    {
        Prepare(Feature("billing", entries: Entry("rounding", new WhenHints { Anchors = [AddAnchor()] })));
        WriteSource(CalculatorPath, Calculator.Replace("var sum = a + b;", "var sum = checked(a + b);"));

        var candidate = Single();

        Assert.That(Codes(candidate), Is.EqualTo(new[] { "ChangedAnchor" }));
        Assert.That(Action(candidate), Is.EqualTo("review"));
        Assert.That(candidate.GetProperty("reasons")[0].GetProperty("message").GetString(), Does.Contain($"{CalculatorPath}:5"));
    }

    [Test]
    public void DeadPathNextToALivePathIsNotAnOrphan()
    {
        Prepare(Feature("billing", entries: Entry("rounding", Paths("src/", "src/Gone.cs"))));

        var candidate = Single();

        Assert.That(Codes(candidate), Is.EqualTo(new[] { "DeadPath" }));
        Assert.That(candidate.GetProperty("reasons")[0].GetProperty("message").GetString(), Does.Contain("'src/Gone.cs'"));
        Assert.That(Action(candidate), Is.EqualTo("re-anchor"));
    }

    [Test]
    public void EntryWhoseHintsMatchNothingIsOrphaned()
    {
        var anchor = AddAnchor();
        Prepare(Feature("billing", entries: Entry("rounding", new WhenHints { Paths = ["lib/", "**/*.py"], Anchors = [anchor] })));
        File.Delete(Path.Combine(repo.Root, CalculatorPath));

        var candidate = Single();

        Assert.That(Codes(candidate), Is.EqualTo(new[] { "Orphaned", "BrokenAnchor", "DeadPath", "DeadPath" }));
        Assert.That(Action(candidate), Is.EqualTo("delete"));
        Assert.That(Command(candidate), Is.EqualTo("docs rm billing/rounding"));
    }

    [Test]
    public void GlobsAndKeywordsAreNotOrphans()
    {
        Prepare(Feature("billing", when: Paths("src/**/*.CS"), entries: Entry("rounding", new WhenHints { Keywords = ["vat"] })));

        Assert.That(Candidates(), Is.Empty);
    }

    [Test]
    public void DeprecatedFeatureWithOrphanedPathsIsADeleteCandidate()
    {
        Prepare(
            Feature("legacy", FeatureStatus.Deprecated, Paths("old/")),
            Feature("billing", when: Paths("lib/")),
            Feature("archive", FeatureStatus.Deprecated, Paths("src/")));

        var candidates = Candidates();

        Assert.That(candidates.Select(Address), Is.EqualTo(new[] { "legacy", "billing" }));
        Assert.That(Codes(candidates[0]), Is.EqualTo(new[] { "DeprecatedOrphan", "DeadPath" }));
        Assert.That(Command(candidates[0]), Is.EqualTo("docs rm legacy"));
        Assert.That(Codes(candidates[1]), Is.EqualTo(new[] { "Orphaned", "DeadPath" }));
    }

    [Test]
    public void UnreadEntriesAreFlaggedPerKindPolicy()
    {
        var old = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Prepare(Feature("billing", entries:
        [
            Fact("stale", new DateOnly(2026, 3, 1), old, old),
            Fact("fresh", new DateOnly(2026, 10, 1), old, old),
            Fact("edited", new DateOnly(2025, 2, 1), old, Stamp),
            Entry("rule", kind: "rule") with { Created = old, Updated = old },
        ]));

        var candidate = Single();

        Assert.That(Address(candidate), Is.EqualTo("billing/stale"));
        Assert.That(Codes(candidate), Is.EqualTo(new[] { "Unread" }));
        Assert.That(candidate.GetProperty("reasons")[0].GetProperty("message").GetString(), Does.Contain("239 days").And.Contain("180"));
        Assert.That(Action(candidate), Is.EqualTo("review"));
        Assert.That(Command(candidate), Is.EqualTo("docs touch billing/stale"));
    }

    [Test]
    public void UnreadFallsBackToCreatedAndSkipsUnstampedEntries()
    {
        var created = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        Prepare(Feature("billing", entries: [Fact("never-read", null, created), Fact("unstamped", null, null)]));

        Assert.That(Candidates("--today", "2026-06-29"), Is.Empty);
        var candidate = Single("--today", "2026-06-30");
        Assert.That(Address(candidate), Is.EqualTo("billing/never-read"));
        Assert.That(candidate.GetProperty("reasons")[0].GetProperty("message").GetString(), Does.Contain("never read"));
    }

    [Test]
    public void KindWithoutUnreadPolicyIsNeverUnread()
    {
        var ancient = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        repo.WriteDocsFile("config.yaml", """
            version: 1
            kinds:
              note: {}
              rule:
                prune: {}

            """);
        Prepare(Feature("billing", entries:
        [
            Entry("note", kind: "note") with { Created = ancient, Updated = ancient },
            Entry("rule", kind: "rule") with { Created = ancient, Updated = ancient },
        ]));

        Assert.That(Candidates(), Is.Empty);
    }

    [Test]
    public void PlanOfAShippedFeatureSuggestsConvertingIt()
    {
        Prepare(Feature("billing", entries: Plan(new PlanStep { Title = "Write the guide" })), Feature("search", FeatureStatus.InProgress, entries: Plan()));

        var candidate = Single();

        Assert.That(Address(candidate), Is.EqualTo("billing/rollout"));
        Assert.That(candidate.GetProperty("kind").GetString(), Is.EqualTo("plan"));
        Assert.That(Codes(candidate), Is.EqualTo(new[] { "ShippedPlan" }));
        Assert.That(Action(candidate), Is.EqualTo("convert-to-decision"));
    }

    [Test]
    public void DoneStepsAreCollapsible()
    {
        WriteIssue(12, "Done");
        WriteIssue(13, "Cancelled");
        WriteIssue(14, "Open");
        Prepare(Feature("billing", FeatureStatus.InProgress, entries: Plan(
            new PlanStep { Title = "Schema", Issues = [12, 13] },
            new PlanStep { Title = "Store", Issues = [14] },
            new PlanStep { Title = "Guide" })));

        var candidate = Single();

        Assert.That(Codes(candidate), Is.EqualTo(new[] { "CollapsibleStep" }));
        Assert.That(candidate.GetProperty("reasons")[0].GetProperty("message").GetString(), Does.Contain("1 of 3").And.Contain("'Schema'").And.Not.Contain("Store"));
        Assert.That(Action(candidate), Is.EqualTo("collapse-steps"));
    }

    [Test]
    public void ShippedPlanWithDoneStepsHasBothReasons()
    {
        WriteIssue(12, "Done");
        Prepare(Feature("billing", entries: Plan(new PlanStep { Title = "Schema", Issues = [12] })));

        Assert.That(Codes(Single()), Is.EqualTo(new[] { "ShippedPlan", "CollapsibleStep" }));
    }

    void PrepareMixed()
    {
        var old = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Prepare(
            Feature("billing", entries: [Entry("zeta", Paths("gone/")), Entry("alpha", Paths("src/", "gone.cs")), Fact("old", null, old)]),
            Feature("search", entries: [Fact("old", null, old), Plan()]));
    }

    [Test]
    public void CandidatesAreSortedByStrongestReasonThenAddress()
    {
        PrepareMixed();

        var addresses = Candidates().Select(Address).ToList();

        Assert.That(addresses, Is.EqualTo(new[] { "billing/zeta", "billing/alpha", "search/rollout", "billing/old", "search/old" }));
        Assert.That(Candidates().Select(Address), Is.EqualTo(addresses));
    }

    [Test]
    public void ReasonAndFeatureFiltersNarrowTheList()
    {
        PrepareMixed();

        Assert.That(Candidates("--reason", "unread").Select(Address), Is.EqualTo(new[] { "billing/old", "search/old" }));
        Assert.That(Candidates("--reason", "dead-path", "--reason", "ShippedPlan").Select(Address), Is.EqualTo(new[] { "billing/zeta", "billing/alpha", "search/rollout" }));
        Assert.That(Candidates("--feature", "search").Select(Address), Is.EqualTo(new[] { "search/rollout", "search/old" }));
        Assert.That(Candidates("--feature", "repo"), Is.Empty);
    }

    [Test]
    public void BadFilterValuesAreErrors()
    {
        PrepareMixed();

        Assert.That(repo.Run("prune", "--feature", "nope").ExitCode, Is.EqualTo(ExitCodes.NotFound));
        Assert.That(repo.Run("prune", "--reason", "stale").ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(repo.Run("prune", "--today", "26.10.2026").ExitCode, Is.EqualTo(ExitCodes.Usage));
    }

    [Test]
    public void JsonHasTheCandidateShape()
    {
        Prepare(Feature("billing", when: Paths("lib/"), entries: Entry("rounding", Paths("lib/"))));

        var candidates = Candidates();

        Assert.That(candidates[0].EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "address", "summary", "reasons", "suggestion" }));
        Assert.That(candidates[0].GetProperty("summary").GetString(), Is.EqualTo("The billing feature"));
        Assert.That(candidates[1].EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "address", "kind", "summary", "reasons", "suggestion" }));
        Assert.That(candidates[1].GetProperty("kind").GetString(), Is.EqualTo("rule"));
        var reason = candidates[1].GetProperty("reasons")[0];
        Assert.That(reason.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "code", "message" }));
        Assert.That(candidates[1].GetProperty("suggestion").EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "action", "command" }));
    }

    [Test]
    public void TextOutputIsATableAndExitsZero()
    {
        PrepareMixed();

        var result = repo.Run("prune", "--today", Today);

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Output, Does.Contain("Orphaned").And.Contain("billing/zeta").And.Contain("docs rm billing/zeta").And.Contain("5 candidate(s)."));
        Assert.That(result.Output.IndexOf("billing/zeta", StringComparison.Ordinal), Is.LessThan(result.Output.IndexOf("search/old", StringComparison.Ordinal)));
    }

    [Test]
    public void PruneWritesNothing()
    {
        PrepareMixed();
        WriteSource(CalculatorPath, "namespace Demo;\n");
        var before = Snapshot();
        var status = repo.Git("status", "--porcelain", "--ignored");

        repo.Run("prune");
        repo.Run("prune", "--json");

        Assert.That(Snapshot(), Is.EqualTo(before));
        Assert.That(repo.Git("status", "--porcelain", "--ignored"), Is.EqualTo(status));
    }

    Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(repo.Folder).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);

    [Test]
    public void PrunerUsesTheGivenDay()
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Prepare(Feature("billing", entries: Fact("fact", null, created)));
        var store = new DocsStore(repo.Root) { Clock = new TestClock(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)) };

        var candidates = DocsPruner.Prune(store.Root, store.Load(), DocsTime.Today(store.Clock), PruneFilter.None);

        Assert.That(candidates.Single().Reasons.Single(), Is.EqualTo(new PruneReason(PruneCode.Unread,
            "Unread for 181 days (last read or change 2026-01-01, never read); 'fact' entries are flagged after 180.")));
    }
}
