using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class PlanStepTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    static FeatureEntry Plan(params PlanStep[] steps) => new()
    {
        Id = "rollout",
        Kind = "plan",
        Summary = "Roll out invoices",
        Steps = [.. steps],
        Body = "Ship behind a flag.\n",
    };

    static DocsSnapshot Snapshot(params FeatureEntry[] entries) =>
        new(new RepoDocs(), DocsConfig.Default(), [new Feature { Id = "billing", Entries = [.. entries] }], []);

    const string PlanYaml = """
        feature: billing
        status: in-progress
        when: {}
        entries:
          - id: rollout
            kind: plan
            summary: Roll out invoices
            steps:
              - title: Schema and store
                issues: [23, 24]
              - title: Write the migration guide
            body: |
              Ship behind a flag.

        """;

    [Test]
    public void StepsRoundTripThroughYaml()
    {
        repo.Store.Save(new Feature
        {
            Id = "billing",
            Status = FeatureStatus.InProgress,
            Entries = [Plan(new PlanStep { Title = "Schema and store", Issues = [23, 24] }, new PlanStep { Title = "Write the migration guide" })],
        });

        var yaml = repo.ReadDocsFile("billing.yaml");
        var loaded = repo.Store.LoadFeature("billing");

        Assert.That(yaml, Is.EqualTo(PlanYaml.ReplaceLineEndings("\n")));
        Assert.That(DocsYaml.Serialize(loaded), Is.EqualTo(yaml));
        Assert.That(loaded.Entries.Single().Steps.Select(step => step.Issues), Is.EqualTo(new[] { new[] { 23, 24 }, Array.Empty<int>() }));
    }

    [Test]
    public void HandWrittenBlockStyleStepsLoad()
    {
        repo.WriteDocsFile("billing.yaml", "feature: billing\nentries:\n  - id: rollout\n    kind: plan\n    summary: s\n    steps:\n      - title: One\n        issues:\n          - 7\n      - title: Two\n        issues:\n");

        var steps = repo.Store.LoadFeature("billing").Entries.Single().Steps;

        Assert.That(steps.Select(step => step.Title), Is.EqualTo(new[] { "One", "Two" }));
        Assert.That(steps[0].Issues, Is.EqualTo(new[] { 7 }));
        Assert.That(steps[1].Issues, Is.Empty);
    }

    [Test]
    public void EntriesWithoutStepsOmitTheKey()
    {
        repo.Store.Save(new Feature { Id = "billing", Entries = [new FeatureEntry { Id = "rounding", Kind = "rule", Summary = "Round half to even" }] });

        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Not.Contain("steps"));
        Assert.That(repo.Store.LoadFeature("billing").Entries.Single().Steps, Is.Empty);
    }

    [TestCase("      - title: One\n        issue: 7\n")]
    [TestCase("      - title: One\n        issues: [seven]\n")]
    [TestCase("      - One\n")]
    public void InvalidStepsAreFileErrors(string steps)
    {
        repo.WriteDocsFile("billing.yaml", "feature: billing\nentries:\n  - id: rollout\n    kind: plan\n    summary: s\n    steps:\n" + steps);

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Features, Is.Empty);
        Assert.That(snapshot.Errors.Single().FileName, Is.EqualTo("billing.yaml"));
    }

    [Test]
    public void PlanWithStepsIsValid()
    {
        Assert.That(DocsValidator.Validate(Snapshot(Plan(new PlanStep { Title = "Schema", Issues = [23] }))), Is.Empty);
    }

    [Test]
    public void StepsOnANonPlanEntryAreAnError()
    {
        var entry = Plan(new PlanStep { Title = "Schema", Issues = [23] }) with { Kind = "decision" };

        var problem = DocsValidator.Validate(Snapshot(entry)).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.StepsOnNonPlan));
        Assert.That(problem.File, Is.EqualTo("billing.yaml"));
        Assert.That(problem.Message, Does.Contain("'billing/rollout'").And.Contain("'plan'"));
    }

    [TestCase(null)]
    [TestCase(" ")]
    [TestCase("Schema\nand store")]
    public void StepWithoutAOneLineTitleIsAnError(string? title)
    {
        var problem = DocsValidator.Validate(Snapshot(Plan(new PlanStep { Title = "Fine" }, new PlanStep { Title = title! }))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.InvalidStep));
        Assert.That(problem.Message, Does.StartWith("Step 2 of 'billing/rollout'"));
    }

    [Test]
    public void StepLinkingANonPositiveIssueNumberIsAnError()
    {
        var problem = DocsValidator.Validate(Snapshot(Plan(new PlanStep { Title = "Schema", Issues = [0] }))).Single();

        Assert.That(problem.Kind, Is.EqualTo(ProblemKind.InvalidStep));
    }
}
