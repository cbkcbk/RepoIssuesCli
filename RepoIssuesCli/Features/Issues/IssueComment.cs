namespace RepoIssuesCli.Features.Issues;

public sealed record IssueComment(string? Author, DateTimeOffset Created, string Text);
