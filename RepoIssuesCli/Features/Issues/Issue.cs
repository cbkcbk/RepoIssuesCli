using System.Text.Json.Serialization;

namespace RepoIssuesCli.Features.Issues;

public sealed record Issue
{
    public required Guid Id { get; init; }
    public required int Number { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IssueStatus Status { get; init; } = IssueStatus.Open;
    public IssuePriority Priority { get; init; } = IssuePriority.Medium;
    public IReadOnlyList<int> BlockedBy { get; init; } = [];
    public IReadOnlyList<SourceLocation> Locations { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset Updated { get; init; }

    [JsonIgnore]
    public string Title => FormatTitle(Number, Name);

    public static string FormatTitle(int number, string name) => $"{number:D4} {name}";

    public static DateTimeOffset Timestamp() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
