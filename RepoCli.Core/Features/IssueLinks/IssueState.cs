namespace RepoCli.Core.Features.IssueLinks;

public enum IssueState
{
    Open,
    InProgress,
    Done,
    Cancelled,
}

public static class IssueStateExtensions
{
    public static bool IsClosed(this IssueState state) => state is IssueState.Done or IssueState.Cancelled;
}
