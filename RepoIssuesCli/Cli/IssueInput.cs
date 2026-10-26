using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Cli;

public static class IssueInput
{
    public static IReadOnlyList<SourceLocation> ParseLocations(IEnumerable<string> values, string repoRoot)
    {
        var locations = new IssueLocations(repoRoot);
        return values.Select(value => locations.Create(ParseLocation(value, repoRoot))).Distinct().ToList();
    }

    public static LocationInput ParseLocation(string value, string repoRoot) =>
        LocationInput.TryParse(value, out var location)
            ? location.RelativeTo(repoRoot, LocationBase(repoRoot))
            : throw new UsageException($"Invalid location '{value}'. Use path, path:line, path:line:col or path:line-end.");

    static string LocationBase(string repoRoot)
    {
        var current = Path.GetFullPath(Environment.CurrentDirectory);
        var relative = Path.GetRelativePath(repoRoot, current);
        var insideRepo = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
        return insideRepo ? current : repoRoot;
    }

    public static IReadOnlyList<string> ParseTags(IEnumerable<string> values) =>
        values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<int> ResolveBlockers(IReadOnlyCollection<Issue> issues, IEnumerable<string> references, int? self = null)
    {
        var numbers = references.Select(reference => IssueResolver.Resolve(issues, reference).Number).Distinct().ToList();
        if (self is { } number && numbers.Contains(number))
            throw new UsageException("An issue cannot block itself.");
        return numbers;
    }

    public static void EnsureNoCycle(IReadOnlyCollection<Issue> issues, Issue issue, IEnumerable<int> blockers)
    {
        foreach (var blocker in blockers.Where(blocker => !issue.BlockedBy.Contains(blocker)))
        {
            if (BlockerGraph.CreatesCycle(issues, issue.Number, blocker))
                throw new UsageException($"Adding {blocker:D4} as a blocker of {issue.Title} would create a cycle.");
        }
    }
}
