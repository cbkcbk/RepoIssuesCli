namespace RepoIssuesCli.Features.Issues;

public enum ProblemKind
{
    InvalidFile,
    DuplicateNumber,
    SelfBlock,
    DanglingBlocker,
    BlockerCycle,
}

public sealed record ValidationProblem(ProblemKind Kind, string Message, string? File = null);

public static class IssueValidator
{
    public static IReadOnlyList<ValidationProblem> Validate(IssueSnapshot snapshot)
    {
        var issues = snapshot.Issues;
        var numbers = issues.Select(issue => issue.Number).ToHashSet();
        var problems = new List<ValidationProblem>();

        problems.AddRange(snapshot.Errors.Select(error =>
            new ValidationProblem(ProblemKind.InvalidFile, error.Message, error.FileName)));

        problems.AddRange(issues
            .GroupBy(issue => issue.Number)
            .Where(group => group.Count() > 1)
            .Select(group => new ValidationProblem(
                ProblemKind.DuplicateNumber,
                $"Number {group.Key:D4} is used by: {string.Join(", ", group.Select(issue => $"'{issue.Name}' ({issue.Id})"))}.")));

        foreach (var issue in issues)
        {
            var file = $"{issue.Id:D}.json";
            if (issue.BlockedBy.Contains(issue.Number))
                problems.Add(new ValidationProblem(ProblemKind.SelfBlock, $"{issue.Title} blocks itself.", file));

            foreach (var missing in issue.BlockedBy.Where(number => !numbers.Contains(number)))
                problems.Add(new ValidationProblem(ProblemKind.DanglingBlocker, $"{issue.Title} is blocked by {missing:D4}, which does not exist.", file));
        }

        problems.AddRange(BlockerGraph.FindCycles(issues)
            .Where(cycle => cycle.Count > 1)
            .Select(cycle => new ValidationProblem(
                ProblemKind.BlockerCycle,
                $"Blocker cycle: {string.Join(" -> ", cycle.Append(cycle[0]).Select(number => number.ToString("D4")))}.")));

        return problems;
    }
}
