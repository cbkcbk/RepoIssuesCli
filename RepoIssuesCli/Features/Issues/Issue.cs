using System.Text.Json.Serialization;

namespace RepoIssuesCli.Features.Issues;

public sealed record Issue
{
    public required Guid Id { get; init; }
    public required int Number { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public IssueStatus Status { get; set; } = IssueStatus.Open;
    public IssuePriority Priority { get; set; } = IssuePriority.Medium;
    public IReadOnlyList<int> BlockedBy { get; set; } = [];
    public IReadOnlyList<SourceLocation> Locations { get; set; } = [];
    public IReadOnlyList<string> Tags { get; set; } = [];
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset Updated { get; init; }
    public IReadOnlyList<IssueComment> Comments { get; set; } = [];

    [JsonIgnore]
    public string Title => FormatTitle(Number, Name);

    public static string FormatTitle(int number, string name) => $"{number:D4} {name}";

    public static DateTimeOffset Timestamp() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
