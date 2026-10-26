using System.Text.Json.Serialization;
using RepoCli.Core.Features.IssueLinks;

namespace RepoDocsCli.Features.Docs;

public sealed record PlanProgress(int Done, int Total, int Cancelled, IReadOnlyList<int> Missing, IReadOnlyList<StepProgress> Steps)
{
    public bool Complete => Total > 0 && Done == Total;

    public static PlanProgress For(FeatureEntry entry, IssueIndex index)
    {
        var steps = entry.Steps.Select(step => StepProgress.For(step, index)).ToList();
        var linked = steps.SelectMany(step => step.Issues).DistinctBy(issue => issue.Number).ToList();

        return new PlanProgress(
            linked.Count(issue => issue.Resolved),
            linked.Count,
            linked.Count(issue => issue.State == LinkedIssueState.Cancelled),
            linked.Where(issue => issue.State == LinkedIssueState.Missing).Select(issue => issue.Number).Order().ToList(),
            steps);
    }
}

public sealed record StepProgress(string Title, StepState State, int Done, int Total, IReadOnlyList<LinkedIssue> Issues)
{
    public static StepProgress For(PlanStep step, IssueIndex index)
    {
        var issues = step.Issues.Distinct().Select(number => LinkedIssue.For(number, index)).ToList();
        return new StepProgress(step.Title ?? "", StateOf(issues), issues.Count(issue => issue.Resolved), issues.Count, issues);
    }

    static StepState StateOf(IReadOnlyList<LinkedIssue> issues)
    {
        var unresolved = issues.Where(issue => !issue.Resolved).ToList();
        if (issues.Count > 0 && unresolved.Count == 0)
            return StepState.Done;
        if (unresolved.Any(issue => issue.State == LinkedIssueState.InProgress))
            return StepState.InProgress;

        var waiting = unresolved.Where(issue => issue.State == LinkedIssueState.Open).ToList();
        return waiting.Count > 0 && waiting.All(issue => issue.Blocked) ? StepState.Blocked : StepState.Open;
    }
}

public sealed record LinkedIssue(int Number, string? Name, LinkedIssueState State, IReadOnlyList<int> BlockedBy)
{
    public bool Blocked => BlockedBy.Count > 0;

    [JsonIgnore]
    public bool Resolved => State is LinkedIssueState.Done or LinkedIssueState.Cancelled;

    public static LinkedIssue For(int number, IssueIndex index)
    {
        if (index.Find(number) is not { } issue)
            return new LinkedIssue(number, null, LinkedIssueState.Missing, []);

        var state = issue.State switch
        {
            IssueState.InProgress => LinkedIssueState.InProgress,
            IssueState.Done => LinkedIssueState.Done,
            IssueState.Cancelled => LinkedIssueState.Cancelled,
            _ => LinkedIssueState.Open,
        };
        return new LinkedIssue(number, issue.Name, state, issue.State.IsClosed() ? [] : index.UnresolvedBlockers(issue));
    }
}

public enum StepState
{
    [JsonStringEnumMemberName("open")]
    Open,

    [JsonStringEnumMemberName("in-progress")]
    InProgress,

    [JsonStringEnumMemberName("blocked")]
    Blocked,

    [JsonStringEnumMemberName("done")]
    Done,
}

public enum LinkedIssueState
{
    [JsonStringEnumMemberName("open")]
    Open,

    [JsonStringEnumMemberName("in-progress")]
    InProgress,

    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("missing")]
    Missing,
}
