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
        IssueValidator.Validate(new IssueSnapshot(issues, []), NoFiles).Select(problem => problem.Kind).ToList();

    static int? NoFiles(string path) => null;

    static IReadOnlyList<ValidationProblem> ValidateLocation(string location, Func<string, int?> lineCount)
    {
        SourceLocation.TryParse(location, out var parsed);
        return IssueValidator.Validate(new IssueSnapshot([Make(1) with { Locations = [parsed!] }], []), lineCount);
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
        Assert.That(IssueValidator.Validate(new IssueSnapshot([Make(1, 5)], []), NoFiles).Single().Severity, Is.EqualTo(Severity.Error));

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
        var problems = IssueValidator.Validate(new IssueSnapshot([Make(1, 3), Make(2, 1), Make(3, 2), Make(4)], []), NoFiles);

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
