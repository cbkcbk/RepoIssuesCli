using System.Text.Json;
using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Cli;

public class CheckCommandTests
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

    const string Notes = """
        # Notes

        Intro paragraph about the tool.
        Setup requires dotnet ten.
        Run the build with binlog enabled.
        Then run all the tests.
        Finally review the output carefully.

        """;

    const string NotesPath = "docs/notes.md";
    const int BuildLine = 5;

    static readonly DateTimeOffset Stamp = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.InitGit();
        repo.Store.Initialize();
        WriteSource(CalculatorPath, Calculator);
        WriteSource(NotesPath, Notes);
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    string ClaudeMdPath => Path.Combine(repo.Root, "CLAUDE.md");

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

    static FeatureEntry Rule(WhenHints? when = null, string kind = "rule") =>
        new() { Id = "rounding", Kind = kind, Summary = "Round half to even", When = when, Created = Stamp, Updated = Stamp };

    static Feature Billing(WhenHints? when = null, FeatureStatus status = FeatureStatus.Shipped, params FeatureEntry[] entries) =>
        new() { Id = "billing", Status = status, Summary = "Invoices", When = when ?? new WhenHints(), Created = Stamp, Updated = Stamp, Entries = [.. entries] };

    static WhenHints Anchored(Anchor anchor) => new() { Anchors = [anchor] };

    Anchor AddAnchor() => Anchor.Load(repo.Root, CalculatorPath, AddLine, AddEndLine);

    Anchor BuildAnchor() => Anchor.Load(repo.Root, NotesPath, BuildLine);

    void Prepare(Feature feature)
    {
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(feature));
        Assert.That(repo.Run("render").ExitCode, Is.Zero);
        Commit();
    }

    Feature LoadBilling() => repo.Store.LoadFeature("billing");

    void WriteIssue(int number, string status) =>
        File.WriteAllText(Path.Combine(repo.Subfolder(".repo-issues"), $"{number:D5}-issue.json"),
            $$"""{ "number": {{number}}, "name": "Issue {{number}}", "status": "{{status}}" }""");

    List<JsonElement> Problems(params string[] args)
    {
        var result = repo.Run(["check", "--json", .. args]);
        return [.. JsonDocument.Parse(result.Output).RootElement.EnumerateArray()];
    }

    static IEnumerable<string?> Kinds(IEnumerable<JsonElement> problems) => problems.Select(problem => problem.GetProperty("kind").GetString());

    [Test]
    public void CleanRepoExitsZero()
    {
        Prepare(Billing(new WhenHints { Paths = ["src/", CalculatorPath], Anchors = [AddAnchor()] }, entries: Rule()));

        var result = repo.Run("check");

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output, Is.EqualTo("No problems found.\n"));
    }

    [Test]
    public void SchemaErrorExitsOne()
    {
        Prepare(Billing(entries: Rule(kind: "tip")));

        var result = repo.Run("check");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(result.Output, Does.Contain("UndeclaredKind").And.Contain("1 error(s) found."));
    }

    [Test]
    public void WarningsOnlyExitZeroUnlessStrict()
    {
        Prepare(Billing(new WhenHints { Paths = ["src/Gone.cs"] }));

        var result = repo.Run("check");

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(result.Output, Does.Contain("DeadPath").And.Contain("1 warning(s) found."));
        Assert.That(repo.Run("check", "--strict").ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
    }

    [Test]
    public void DeadPathsAreReportedForFeaturesAndEntries()
    {
        Prepare(Billing(new WhenHints { Paths = ["lib/", "src/"] }, entries: Rule(new WhenHints { Paths = ["src/Calculator.cs/", "src/Missing.cs"] })));

        var problems = Problems();

        Assert.That(Kinds(problems), Is.EqualTo(new[] { "DeadPath", "DeadPath", "DeadPath" }));
        Assert.That(problems.Select(problem => problem.GetProperty("address").GetString()), Is.EqualTo(new[] { "billing", "billing/rounding", "billing/rounding" }));
        Assert.That(problems[0].GetProperty("message").GetString(), Does.Contain("'lib/'"));
    }

    [Test]
    public void MovedAnchorIsAFixableWarning()
    {
        var anchor = AddAnchor();
        Prepare(Billing(entries: Rule(Anchored(anchor))));
        WriteSource(CalculatorPath, "using System;\n\n" + Calculator);
        var before = repo.ReadDocsFile("billing.yaml");

        var problem = Problems().Single();

        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("MovedAnchor"));
        Assert.That(problem.GetProperty("severity").GetString(), Is.EqualTo("Warning"));
        Assert.That(problem.GetProperty("fixable").GetBoolean(), Is.True);
        Assert.That(problem.GetProperty("address").GetString(), Is.EqualTo("billing/rounding"));
        Assert.That(problem.GetProperty("file").GetString(), Is.EqualTo("billing.yaml"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    [Test]
    public void FixRewritesMovedAnchorsWithoutBumpingUpdated()
    {
        var anchor = AddAnchor();
        Prepare(Billing(Anchored(anchor), entries: Rule(Anchored(anchor))));
        WriteSource(CalculatorPath, "using System;\n\n" + Calculator);

        var result = repo.Run("check", "--fix");

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output, Does.Contain("Fixed MovedAnchor billing:").And.Contain("Fixed MovedAnchor billing/rounding:").And.Contain("No problems found."));
        var feature = LoadBilling();
        Assert.That(feature.When.Anchors.Single(), Is.EqualTo(anchor with { Line = AddLine + 2 }));
        Assert.That(feature.Entries.Single().When!.Anchors.Single(), Is.EqualTo(anchor with { Line = AddLine + 2 }));
        Assert.That(feature.Updated, Is.EqualTo(Stamp));
        Assert.That(feature.Entries.Single().Updated, Is.EqualTo(Stamp));
        Assert.That(repo.Run("check").Output, Is.EqualTo("No problems found.\n"));
    }

    [Test]
    public void FixOfARenamedFileDoesNotBumpUpdatedEither()
    {
        var anchor = AddAnchor();
        Prepare(Billing(entries: Rule(Anchored(anchor))));
        repo.Git("mv", CalculatorPath, "src/Arithmetic.cs");
        Commit();

        var result = repo.Run("check", "--fix");

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var entry = LoadBilling().Entries.Single();
        Assert.That(entry.When!.Anchors.Single().Path, Is.EqualTo("src/Arithmetic.cs"));
        Assert.That(entry.Updated, Is.EqualTo(Stamp));
    }

    [Test]
    public void ChangedAnchorIsAWarningThatFixLeavesAlone()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        WriteSource(CalculatorPath, Calculator.Replace("var sum = a + b;", "var sum = checked(a + b);"));
        var before = repo.ReadDocsFile("billing.yaml");

        var problem = Problems().Single();
        var fix = repo.Run("check", "--fix");

        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("ChangedAnchor"));
        Assert.That(problem.GetProperty("fixable").GetBoolean(), Is.False);
        Assert.That(fix.ExitCode, Is.Zero);
        Assert.That(fix.Output, Does.Contain("Still needs a human or agent:").And.Contain("ChangedAnchor").And.Not.Contain("Fixed"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    [Test]
    public void BrokenAnchorIsAnErrorThatFixLeavesAlone()
    {
        Prepare(Billing(entries: Rule(Anchored(AddAnchor()))));
        File.Delete(Path.Combine(repo.Root, CalculatorPath));
        var before = repo.ReadDocsFile("billing.yaml");

        var check = repo.Run("check");
        var fix = repo.Run("check", "--fix");

        Assert.That(check.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(check.Output, Does.Contain("BrokenAnchor").And.Contain("file not found"));
        Assert.That(fix.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    [Test]
    public void FuzzyAnchorNeedsConfirmationAndIsNotFixed()
    {
        Prepare(Billing(entries: Rule(Anchored(BuildAnchor()))));
        WriteSource(NotesPath, "Preface.\n" + Notes.Replace("Setup requires dotnet ten.", "Setup requires dotnet ten or newer."));
        var before = repo.ReadDocsFile("billing.yaml");

        var problem = Problems().Single();
        var fixedProblems = JsonDocument.Parse(repo.Run("check", "--fix", "--json").Output).RootElement.GetProperty("fixed");

        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("FuzzyAnchor"));
        Assert.That(problem.GetProperty("severity").GetString(), Is.EqualTo("Warning"));
        Assert.That(problem.GetProperty("fixable").GetBoolean(), Is.False);
        Assert.That(fixedProblems.GetArrayLength(), Is.Zero);
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    static FeatureEntry Plan(params int[] issues) => new()
    {
        Id = "rollout", Kind = PlanStep.EntryKind, Summary = "Roll out", Created = Stamp, Updated = Stamp,
        Steps = [new PlanStep { Title = "Build it", Issues = [.. issues] }],
    };

    [Test]
    public void PlannedFeatureWithAnInProgressIssueIsAMismatch()
    {
        WriteIssue(12, "InProgress");
        WriteIssue(13, "Open");
        Prepare(Billing(status: FeatureStatus.Planned, entries: Plan(12, 13)));

        var problem = Problems().Single();

        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("StatusMismatch"));
        Assert.That(problem.GetProperty("address").GetString(), Is.EqualTo("billing"));
        Assert.That(problem.GetProperty("message").GetString(), Does.Contain("planned").And.Contain("0012").And.Not.Contain("0013"));
    }

    [Test]
    public void ShippedFeatureWithOpenIssuesIsAMismatch()
    {
        WriteIssue(12, "Done");
        WriteIssue(13, "Open");
        WriteIssue(14, "Cancelled");
        Prepare(Billing(entries: Plan(12, 13, 14)));

        var problem = Problems().Single();

        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("StatusMismatch"));
        Assert.That(problem.GetProperty("message").GetString(), Does.Contain("shipped").And.Contain("0013").And.Not.Contain("0012").And.Not.Contain("0014"));
    }

    [Test]
    public void MatchingStatusesAreFine()
    {
        WriteIssue(12, "Done");
        WriteIssue(13, "Open");
        Prepare(Billing(status: FeatureStatus.Planned, entries: Plan(13)));

        Assert.That(Problems(), Is.Empty);

        Prepare(Billing(entries: Plan(12)));
        Assert.That(Problems(), Is.Empty);
    }

    [Test]
    public void MissingIssueNumbersAreWarnings()
    {
        WriteIssue(12, "Done");
        Prepare(Billing(entries: Plan(12, 99)));

        var result = repo.Run("check");
        var problem = Problems().Single();

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("MissingIssue"));
        Assert.That(problem.GetProperty("address").GetString(), Is.EqualTo("billing/rollout"));
        Assert.That(problem.GetProperty("message").GetString(), Does.Contain("0099"));
    }

    [Test]
    public void StaleClaudeMdIsAWarningAndFixReRendersIt()
    {
        Prepare(Billing());
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(Billing() with { Summary = "Payments" }));

        var problem = Problems().Single();
        var fix = repo.Run("check", "--fix");

        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("StaleClaudeMd"));
        Assert.That(problem.GetProperty("severity").GetString(), Is.EqualTo("Warning"));
        Assert.That(problem.GetProperty("fixable").GetBoolean(), Is.True);
        Assert.That(fix.ExitCode, Is.Zero, fix.Output);
        Assert.That(fix.Output, Does.Contain("Fixed StaleClaudeMd CLAUDE.md"));
        Assert.That(File.ReadAllText(ClaudeMdPath), Does.Contain("- billing [shipped]: Payments"));
        Assert.That(repo.Run("render", "--check").ExitCode, Is.Zero);
    }

    [Test]
    public void MalformedClaudeMdMarkersAreAnInvalidFileError()
    {
        Prepare(Billing());
        File.WriteAllText(ClaudeMdPath, "<!-- docs:begin -->\nno end\n");

        var check = repo.Run("check", "--json");
        var fix = repo.Run("check", "--fix");

        var problem = JsonDocument.Parse(check.Output).RootElement.EnumerateArray().Single();
        Assert.That(check.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(problem.GetProperty("kind").GetString(), Is.EqualTo("InvalidFile"));
        Assert.That(problem.GetProperty("file").GetString(), Is.EqualTo("CLAUDE.md"));
        Assert.That(problem.GetProperty("severity").GetString(), Is.EqualTo("Error"));
        Assert.That(fix.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(File.ReadAllText(ClaudeMdPath), Is.EqualTo("<!-- docs:begin -->\nno end\n"));
    }

    [Test]
    public void MergeConflictIsAnErrorThatFixMerges()
    {
        Prepare(Billing());
        repo.WriteDocsFile("billing.yaml", """
            feature: billing
            status: shipped
            <<<<<<< ours
            summary: Invoices
            =======
            summary: Payments
            >>>>>>> theirs
            created: 2026-10-01T12:00:00Z

            """);

        var problems = Problems();
        var fix = JsonDocument.Parse(repo.Run("check", "--fix", "--json").Output).RootElement;

        Assert.That(Kinds(problems), Is.EqualTo(new[] { "MergeConflict" }));
        Assert.That(problems[0].GetProperty("severity").GetString(), Is.EqualTo("Error"));
        Assert.That(problems[0].GetProperty("fixable").GetBoolean(), Is.True);
        Assert.That(Kinds(fix.GetProperty("fixed").EnumerateArray()), Does.Contain("MergeConflict"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Not.Contain("<<<<<<<"));
        Assert.That(Kinds(fix.GetProperty("problems").EnumerateArray()), Does.Not.Contain("MergeConflict").And.Not.Contain("InvalidFile"));
    }

    [Test]
    public void FixJsonListsFixedAndRemainingProblems()
    {
        Prepare(Billing(new WhenHints { Paths = ["lib/"], Anchors = [AddAnchor()] }));
        WriteSource(CalculatorPath, "// header\n" + Calculator);

        var result = repo.Run("check", "--fix", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(json.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "fixed", "problems" }));
        var applied = json.GetProperty("fixed").EnumerateArray().Single();
        Assert.That(applied.EnumerateObject().Select(property => property.Name),
            Is.EqualTo(new[] { "kind", "message", "file", "address", "severity", "fixable" }));
        Assert.That(applied.GetProperty("kind").GetString(), Is.EqualTo("MovedAnchor"));
        Assert.That(applied.GetProperty("message").GetString(), Does.Contain($"{CalculatorPath}:{AddLine + 1}"));
        Assert.That(Kinds(json.GetProperty("problems").EnumerateArray()), Is.EqualTo(new[] { "DeadPath" }));
    }

    [Test]
    public void JsonIsAnArrayWithoutFix()
    {
        Prepare(Billing(entries: Rule(kind: "tip")));

        var problem = Problems().Single();

        Assert.That(problem.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "kind", "message", "file", "severity", "fixable" }));
        Assert.That(problem.GetProperty("severity").GetString(), Is.EqualTo("Error"));
    }

    [Test]
    public void ValidateIsAnAlias()
    {
        Prepare(Billing(new WhenHints { Paths = ["lib/"] }));

        var result = repo.Run("validate", "--strict");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(result.Output, Does.Contain("DeadPath"));
    }

    [Test]
    public void CheckWithoutRepoDocsIsAUsageError()
    {
        Directory.Delete(repo.Folder, recursive: true);

        var result = repo.Run("check");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("docs init"));
    }
}
