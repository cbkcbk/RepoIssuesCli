using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class DocsValidatorTests
{
    static FeatureEntry Entry(string id, string kind = "rule", string summary = "Round half to even") =>
        new() { Id = id, Kind = kind, Summary = summary };

    static DocsSnapshot Snapshot(params FeatureEntry[] entries) => Snapshot(DocsConfig.Default(), entries);

    static DocsSnapshot Snapshot(DocsConfig config, params FeatureEntry[] entries) =>
        new(new RepoDocs(), config, [new Feature { Id = "billing", Entries = [.. entries] }], []);

    static IReadOnlyList<ValidationProblem> Validate(DocsSnapshot snapshot) => DocsValidator.Validate(snapshot);

    [Test]
    public void ValidSnapshotHasNoProblems()
    {
        var problems = Validate(Snapshot(Entry("rounding"), Entry("vat-cache", "gotcha")));

        Assert.That(problems, Is.Empty);
    }

    [Test]
    public void UndeclaredKindIsAnError()
    {
        var problem = Validate(Snapshot(Entry("rounding", "tip"))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.UndeclaredKind));
        Assert.That(problem.Severity, Is.EqualTo(Severity.Error));
        Assert.That(problem.File, Is.EqualTo("billing.yaml"));
        Assert.That(problem.Message, Does.Contain("'billing/rounding'").And.Contain("'tip'"));
    }

    [Test]
    public void KindsComeFromTheConfig()
    {
        var config = new DocsConfig { Kinds = new Dictionary<string, KindConfig> { ["runbook"] = new() } };

        var problems = Validate(Snapshot(config, Entry("restart", "runbook"), Entry("rounding", "rule")));

        Assert.That(problems.Select(problem => problem.Kind), Is.EqualTo(new[] { ProblemKind.UndeclaredKind }));
        Assert.That(problems[0].Message, Does.Contain("'billing/rounding'"));
    }

    [Test]
    public void UndeclaredKindInAHandWrittenConfigIsReported()
    {
        using var repo = new TempRepo();
        repo.WriteDocsFile("config.yaml", "version: 1\nkinds:\n  runbook: {}\n");
        repo.WriteDocsFile("billing.yaml", "feature: billing\nentries:\n  - id: rounding\n    kind: rule\n    summary: Round half to even\n");

        var problem = Validate(repo.Store.Load()).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.UndeclaredKind));
        Assert.That(problem.File, Is.EqualTo("billing.yaml"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("  ")]
    public void MissingSummaryIsAnError(string? summary)
    {
        var problem = Validate(Snapshot(Entry("rounding", summary: summary!))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.MissingSummary));
    }

    [Test]
    public void MultiLineSummaryIsAnError()
    {
        var problem = Validate(Snapshot(Entry("rounding", summary: "Round half to even.\nNever round twice."))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.MultiLineSummary));
    }

    [Test]
    public void TrailingNewlineInSummaryIsNotMultiLine()
    {
        Assert.That(Validate(Snapshot(Entry("rounding", summary: "Round half to even\n"))), Is.Empty);
    }

    [Test]
    public void MultiLineFeatureSummaryIsAnError()
    {
        var snapshot = new DocsSnapshot(new RepoDocs(), DocsConfig.Default(), [new Feature { Id = "billing", Summary = "Invoices\nand payments" }], []);

        Assert.That(Validate(snapshot).Single().Kind, Is.EqualTo(ProblemKind.MultiLineSummary));
    }

    [Test]
    public void DuplicateEntryIdsWithinAFeatureAreAnError()
    {
        var problem = Validate(Snapshot(Entry("rounding"), Entry("rounding", "fact"))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.DuplicateEntryId));
        Assert.That(problem.Message, Does.Contain("'billing/rounding'"));
    }

    [Test]
    public void SameEntryIdInDifferentFeaturesIsFine()
    {
        var snapshot = new DocsSnapshot(
            new RepoDocs { Entries = [Entry("rounding")] },
            DocsConfig.Default(),
            [new Feature { Id = "billing", Entries = [Entry("rounding")] }, new Feature { Id = "search", Entries = [Entry("rounding")] }],
            []);

        Assert.That(Validate(snapshot), Is.Empty);
    }

    [TestCase("Rounding")]
    [TestCase("round_half")]
    [TestCase("round--half")]
    [TestCase("-round")]
    [TestCase("billing/rounding")]
    [TestCase("")]
    [TestCase(null)]
    public void InvalidEntryIdIsAnError(string? id)
    {
        var problem = Validate(Snapshot(Entry(id!))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.InvalidEntryId));
    }

    [Test]
    public void RepoEntriesAreValidatedAgainstRepoYaml()
    {
        var snapshot = new DocsSnapshot(new RepoDocs { Entries = [Entry("style", "tip"), Entry("style")] }, DocsConfig.Default(), [], []);

        var problems = Validate(snapshot);

        Assert.That(problems.Select(problem => problem.Kind), Is.EquivalentTo(new[] { ProblemKind.UndeclaredKind, ProblemKind.DuplicateEntryId }));
        Assert.That(problems.Select(problem => problem.File), Has.All.EqualTo("repo.yaml"));
        Assert.That(problems[0].Message, Does.Contain("'repo/style'"));
    }

    [Test]
    public void FileErrorsBecomeInvalidFileProblems()
    {
        var snapshot = new DocsSnapshot(new RepoDocs(), DocsConfig.Default(), [], [new DocsFileError("broken.yaml", "Bad YAML.")]);

        var problem = Validate(snapshot).Single();

        Assert.That(problem, Is.EqualTo(new ValidationProblem(ProblemKind.InvalidFile, "Bad YAML.", "broken.yaml")));
    }

    [Test]
    public void EntryIdsAreAddressedAsFeatureSlashEntry()
    {
        Assert.That(EntryId.Address("billing", "rounding"), Is.EqualTo("billing/rounding"));
        Assert.That(EntryId.IsValid("vat-cache-2"), Is.True);
    }
}
