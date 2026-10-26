using RepoCli.Core.Features.IssueLinks;
using RepoDocsCli.Cli;
using RepoDocsCli.Features.Docs;
using Spectre.Console.Testing;

namespace RepoDocsCli.Tests.Features;

public class PlanProgressTests
{
    static readonly IssueIndex Index = new(
    [
        Issue(1, "Schema", IssueState.Done),
        Issue(2, "Old schema", IssueState.Cancelled),
        Issue(3, "Store", IssueState.InProgress),
        Issue(4, "Wire commands", IssueState.Open, 5),
        Issue(5, "Settings", IssueState.Open),
        Issue(6, "Write docs", IssueState.Open, 1),
        Issue(7, "Publish", IssueState.Open, 99),
    ], []);

    static IssueSummary Issue(int number, string name, IssueState state, params int[] blockedBy) =>
        new(number, name, state, blockedBy, $"{number:D5}.json");

    static PlanStep Step(string title, params int[] issues) => new() { Title = title, Issues = [.. issues] };

    static FeatureEntry Plan(params PlanStep[] steps) => new() { Id = "rollout", Kind = "plan", Summary = "Roll out invoices", Steps = [.. steps] };

    static FeatureEntry FullPlan() => Plan(
        Step("Schema", 1, 2),
        Step("Store", 3, 4),
        Step("Wiring", 4, 7),
        Step("Docs", 5, 6),
        Step("Cleanup", 1, 42),
        Step("Later"));

    static StepProgress StepOf(PlanProgress progress, string title) => progress.Steps.Single(step => step.Title == title);

    [Test]
    public void CountsDistinctLinkedIssuesWithCancelledAsResolved()
    {
        var progress = PlanProgress.For(FullPlan(), Index);

        Assert.That((progress.Done, progress.Total, progress.Cancelled), Is.EqualTo((2, 8, 1)));
        Assert.That(progress.Missing, Is.EqualTo(new[] { 42 }));
        Assert.That(progress.Complete, Is.False);
    }

    [Test]
    public void StepStatesFollowTheirIssues()
    {
        var progress = PlanProgress.For(FullPlan(), Index);

        Assert.That(progress.Steps.Select(step => (step.Title, step.State, step.Done, step.Total)), Is.EqualTo(new[]
        {
            ("Schema", StepState.Done, 2, 2),
            ("Store", StepState.InProgress, 0, 2),
            ("Wiring", StepState.Blocked, 0, 2),
            ("Docs", StepState.Open, 0, 2),
            ("Cleanup", StepState.Open, 1, 2),
            ("Later", StepState.Open, 0, 0),
        }));
    }

    [Test]
    public void BlockedMeansAnOpenOrMissingBlocker()
    {
        var progress = PlanProgress.For(FullPlan(), Index);
        var wiring = StepOf(progress, "Wiring");

        Assert.That(wiring.Issues.Select(issue => (issue.Number, issue.Blocked)), Is.EqualTo(new[] { (4, true), (7, true) }));
        Assert.That(wiring.Issues[0].BlockedBy, Is.EqualTo(new[] { 5 }));
        Assert.That(wiring.Issues[1].BlockedBy, Is.EqualTo(new[] { 99 }));
        Assert.That(StepOf(progress, "Docs").Issues.Single(issue => issue.Number == 6).Blocked, Is.False);
    }

    [Test]
    public void MissingIssuesAreFlaggedAndKeepTheStepOpen()
    {
        var missing = StepOf(PlanProgress.For(FullPlan(), Index), "Cleanup").Issues.Single(issue => issue.Number == 42);

        Assert.That((missing.State, missing.Name, missing.Blocked), Is.EqualTo((LinkedIssueState.Missing, (string?)null, false)));
        Assert.That(PlanProgress.For(Plan(Step("Gone", 42)), Index).Steps.Single().State, Is.EqualTo(StepState.Open));
    }

    [Test]
    public void AllCancelledStepIsDoneAndShowsTheCancellations()
    {
        var progress = PlanProgress.For(Plan(Step("Dropped", 2)), Index);

        Assert.That(progress.Steps.Single().State, Is.EqualTo(StepState.Done));
        Assert.That(progress.Steps.Single().Issues.Single().State, Is.EqualTo(LinkedIssueState.Cancelled));
        Assert.That((progress.Done, progress.Total, progress.Cancelled, progress.Complete), Is.EqualTo((1, 1, 1, true)));
    }

    [Test]
    public void InProgressWinsOverBlocked()
    {
        var index = new IssueIndex([Issue(1, "Started", IssueState.InProgress, 2), Issue(2, "Blocker", IssueState.Open)], []);

        Assert.That(PlanProgress.For(Plan(Step("Mixed", 1, 2)), index).Steps.Single().State, Is.EqualTo(StepState.InProgress));
    }

    [Test]
    public void StepIsBlockedOnlyWhenEveryOpenIssueIsBlocked()
    {
        Assert.That(PlanProgress.For(Plan(Step("Mixed", 4, 5)), Index).Steps.Single().State, Is.EqualTo(StepState.Open));
        Assert.That(PlanProgress.For(Plan(Step("Partly done", 1, 4)), Index).Steps.Single().State, Is.EqualTo(StepState.Blocked));
    }

    [Test]
    public void PlanWithoutStepsHasNoProgress()
    {
        var progress = PlanProgress.For(Plan(), Index);

        Assert.That((progress.Done, progress.Total, progress.Complete), Is.EqualTo((0, 0, false)));
        Assert.That(progress.Steps, Is.Empty);
    }

    [Test]
    public void ProgressIsReadFromRepoIssues()
    {
        using var repo = new TempRepo();
        var folder = repo.Subfolder(".repo-issues");
        File.WriteAllText(Path.Combine(folder, "00001-schema.json"), """{ "number": 1, "name": "Schema", "status": "Done" }""");
        File.WriteAllText(Path.Combine(folder, "00002-store.json"), """{ "number": 2, "name": "Store", "status": "Open", "blockedBy": [1] }""");

        var progress = PlanProgress.For(Plan(Step("All", 1, 2, 3)), IssueIndex.Load(repo.Root));

        Assert.That((progress.Done, progress.Total), Is.EqualTo((1, 3)));
        Assert.That(progress.Missing, Is.EqualTo(new[] { 3 }));
        Assert.That(progress.Steps.Single().State, Is.EqualTo(StepState.Open));
    }

    [Test]
    public void JsonShapeUsesKebabCaseStates()
    {
        var json = DocsJson.SerializeOutput(PlanProgress.For(Plan(Step("Wiring", 4, 42), Step("Store", 3)), Index));

        Assert.That(json.ReplaceLineEndings("\n"), Is.EqualTo("""
            {
              "done": 0,
              "total": 3,
              "cancelled": 0,
              "missing": [
                42
              ],
              "steps": [
                {
                  "title": "Wiring",
                  "state": "blocked",
                  "done": 0,
                  "total": 2,
                  "issues": [
                    {
                      "number": 4,
                      "name": "Wire commands",
                      "state": "open",
                      "blockedBy": [
                        5
                      ],
                      "blocked": true
                    },
                    {
                      "number": 42,
                      "state": "missing",
                      "blockedBy": [],
                      "blocked": false
                    }
                  ]
                },
                {
                  "title": "Store",
                  "state": "in-progress",
                  "done": 0,
                  "total": 1,
                  "issues": [
                    {
                      "number": 3,
                      "name": "Store",
                      "state": "in-progress",
                      "blockedBy": [],
                      "blocked": false
                    }
                  ]
                }
              ],
              "complete": false
            }

            """.ReplaceLineEndings("\n")));
    }

    [Test]
    public void RendererShowsStepsStatesAndMarkers()
    {
        var console = new TestConsole().Width(120);

        PlanRenderer.Write(console, "billing", FullPlan(), PlanProgress.For(FullPlan(), Index));

        Assert.That(console.Output.ReplaceLineEndings("\n"), Is.EqualTo("""
            billing/rollout Roll out invoices 2/8 (1 cancelled) missing 0042
            ├── done Schema 2/2
            │   ├── 0001 Schema Done
            │   └── 0002 Old schema Cancelled
            ├── in-progress Store 0/2
            │   ├── 0003 Store InProgress
            │   └── 0004 Wire commands Open blocked by 0005
            ├── blocked Wiring 0/2
            │   ├── 0004 Wire commands Open blocked by 0005
            │   └── 0007 Publish Open blocked by 0099
            ├── open Docs 0/2
            │   ├── 0005 Settings Open
            │   └── 0006 Write docs Open
            ├── open Cleanup 1/2
            │   ├── 0001 Schema Done
            │   └── 0042 (missing)
            └── open Later 0/0
                └── No linked issues.

            """.ReplaceLineEndings("\n")));
    }

    [Test]
    public void RendererSaysWhenThereAreNoSteps()
    {
        var console = new TestConsole().Width(120);

        PlanRenderer.Write(console, "billing", Plan(), PlanProgress.For(Plan(), Index));

        Assert.That(console.Output, Does.Contain("billing/rollout Roll out invoices 0/0").And.Contain("No steps."));
    }
}
