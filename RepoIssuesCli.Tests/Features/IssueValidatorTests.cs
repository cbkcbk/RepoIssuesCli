using RepoCli.Core.Features.Anchors;
using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Features;

public class IssueValidatorTests
{
    static Issue Make(int number, params int[] blockedBy) => new()
    {
        Id = Guid.CreateVersion7(),
        Number = number,
        Name = $"Issue {number}",
        BlockedBy = blockedBy,
        Created = Issue.Timestamp(),
        Updated = Issue.Timestamp(),
    };

    static IReadOnlyList<ProblemKind> Kinds(params Issue[] issues) =>
        Validate(issues).Select(problem => problem.Kind).ToList();

    static IReadOnlyList<ValidationProblem> Validate(params Issue[] issues) =>
        IssueValidator.Validate(new IssueSnapshot(issues, []), NoFiles, AlwaysOk);

    static int? NoFiles(string path) => null;

    static AnchorResolution AlwaysOk(Anchor anchor) => new(AnchorState.Ok, anchor, anchor, AnchorSearch.Hint, "ok");

    static IReadOnlyList<ValidationProblem> ValidateLocation(string location, Func<string, int?> lineCount)
    {
        LocationInput.TryParse(location, out var parsed);
        var plain = new SourceLocation(parsed!.Path, parsed.Line, parsed.Column);
        return IssueValidator.Validate(new IssueSnapshot([Make(1) with { Locations = [plain] }], []), lineCount, AlwaysOk);
    }

    static readonly Anchor Stored = new("src/a.cs", 10, 2, "context", "body", "Calculator.Add");

    static ValidationProblem? ValidateAnchor(AnchorState state, AnchorSearch foundBy, Anchor? resolved) =>
        IssueValidator.Validate(
            new IssueSnapshot([Make(1) with { Locations = [SourceLocation.From(Stored)] }], []),
            NoFiles,
            anchor => new AnchorResolution(state, anchor, resolved, foundBy, "reason", foundBy == AnchorSearch.Fuzzy ? 0.8 : null)).SingleOrDefault();

    [Test]
    public void OkAnchorHasNoProblemsEvenWithoutLineCounts() =>
        Assert.That(ValidateAnchor(AnchorState.Ok, AnchorSearch.Hint, Stored), Is.Null);

    [TestCase(AnchorState.Moved, ProblemKind.LocationMoved)]
    [TestCase(AnchorState.Changed, ProblemKind.LocationChanged)]
    [TestCase(AnchorState.Broken, ProblemKind.LocationBroken)]
    public void AnchorResultsAreWarnings(AnchorState state, ProblemKind kind)
    {
        var resolved = state == AnchorState.Broken ? null : Stored with { Line = 14 };

        var problem = ValidateAnchor(state, state == AnchorState.Broken ? AnchorSearch.None : AnchorSearch.SameFile, resolved)!;

        Assert.Multiple(() =>
        {
            Assert.That(problem.Kind, Is.EqualTo(kind));
            Assert.That(problem.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(problem.File, Is.EqualTo("00001-issue_1.json"));
            Assert.That(problem.Location, Is.EqualTo("src/a.cs:10"));
            Assert.That(problem.Message, Does.Contain("src/a.cs:10 (2 lines) in Calculator.Add").And.Contain("reason"));
            Assert.That(problem.ResolvedTo, Is.EqualTo(resolved is null ? null : "src/a.cs:14"));
            Assert.That(problem.NeedsConfirmation, Is.Null);
        });
    }

    [Test]
    public void MovedAnchorSuggestsFix() =>
        Assert.That(ValidateAnchor(AnchorState.Moved, AnchorSearch.SameFile, Stored with { Line = 14 })!.Message, Does.Contain("validate --fix"));

    [Test]
    public void FuzzyResultNeedsConfirmation()
    {
        var problem = ValidateAnchor(AnchorState.Moved, AnchorSearch.Fuzzy, Stored with { Line = 14 })!;

        Assert.That(problem.NeedsConfirmation, Is.True);
        Assert.That(problem.Message, Does.Contain("Needs confirmation").And.Not.Contain("--fix"));
    }

    [Test]
    public void ExistingLocationHasNoProblems() =>
        Assert.That(ValidateLocation("src/a.cs:10:2", path => path == "src/a.cs" ? 10 : null), Is.Empty);

    [Test]
    public void MissingLocationFileIsWarning()
    {
        var problems = ValidateLocation("src/gone.cs:3", NoFiles);

        Assert.That(problems.Select(problem => (problem.Kind, problem.Severity)),
            Is.EqualTo([(ProblemKind.MissingLocationFile, Severity.Warning)]));
        Assert.That(problems[0].Message, Does.Contain("src/gone.cs:3"));
        Assert.That(problems[0].File, Is.EqualTo("00001-issue_1.json"));
    }

    [Test]
    public void LineBeyondEndOfFileIsWarning()
    {
        var problems = ValidateLocation("src/a.cs:11", path => 10);

        Assert.That(problems.Select(problem => (problem.Kind, problem.Severity)),
            Is.EqualTo([(ProblemKind.LocationLineOutOfRange, Severity.Warning)]));
        Assert.That(problems[0].Message, Does.Contain("10 lines"));
    }

    [Test]
    public void LocationWithoutLineOnlyNeedsThePath() =>
        Assert.That(ValidateLocation("src", path => 0), Is.Empty);

    [Test]
    public void StructuralProblemsAreErrors() =>
        Assert.That(Validate(Make(1, 5)).Single().Severity, Is.EqualTo(Severity.Error));

    [Test]
    public void ValidGraphHasNoProblems() => Assert.That(Kinds(Make(1), Make(2, 1), Make(3, 1, 2)), Is.Empty);

    [Test]
    public void DetectsDuplicateNumbers() => Assert.That(Kinds(Make(1), Make(1)), Is.EqualTo([ProblemKind.DuplicateNumber]));

    [Test]
    public void DetectsDanglingBlocker() => Assert.That(Kinds(Make(1, 5)), Is.EqualTo([ProblemKind.DanglingBlocker]));

    [Test]
    public void DetectsSelfBlock() => Assert.That(Kinds(Make(1, 1)), Is.EqualTo([ProblemKind.SelfBlock]));

    [Test]
    public void ReportsEachCycleOnce()
    {
        var problems = Validate(Make(1, 3), Make(2, 1), Make(3, 2), Make(4));

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0].Kind, Is.EqualTo(ProblemKind.BlockerCycle));
        Assert.That(problems[0].Message, Does.Contain("0001 -> 0003 -> 0002 -> 0001"));
    }

    [Test]
    public void CycleCheckForNewBlocker()
    {
        Issue[] issues = [Make(1), Make(2, 1), Make(3, 2)];

        Assert.That(BlockerGraph.CreatesCycle(issues, issueNumber: 1, blockerNumber: 3), Is.True);
        Assert.That(BlockerGraph.CreatesCycle(issues, issueNumber: 3, blockerNumber: 1), Is.False);
    }
}
