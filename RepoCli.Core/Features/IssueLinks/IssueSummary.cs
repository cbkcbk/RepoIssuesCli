namespace RepoCli.Core.Features.IssueLinks;

public sealed record IssueSummary(int Number, string Name, IssueState State, IReadOnlyList<int> BlockedBy, string FileName);
