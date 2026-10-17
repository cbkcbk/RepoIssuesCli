namespace RepoIssuesCli.Features.Issues;

public enum IssueStatus
{
    Open,
    InProgress,
    Done,
    Cancelled,
}

public static class IssueStatusExtensions
{
    public static bool IsClosed(this IssueStatus status) => status is IssueStatus.Done or IssueStatus.Cancelled;
}
