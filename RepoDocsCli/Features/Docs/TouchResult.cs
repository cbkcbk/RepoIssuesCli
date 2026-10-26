namespace RepoDocsCli.Features.Docs;

public sealed record TouchResult(string? Target, DateTimeOffset? Updated, IReadOnlyList<string> Files);
