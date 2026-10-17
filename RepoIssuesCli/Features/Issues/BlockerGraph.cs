namespace RepoIssuesCli.Features.Issues;

public static class BlockerGraph
{
    public static IReadOnlyList<Issue> OpenBlockers(Issue issue, IReadOnlyCollection<Issue> issues) =>
        issue.BlockedBy
            .Select(number => issues.FirstOrDefault(candidate => candidate.Number == number))
            .OfType<Issue>()
            .Where(blocker => !blocker.Status.IsClosed())
            .ToList();

    public static IReadOnlyList<int> MissingBlockers(Issue issue, IReadOnlyCollection<Issue> issues) =>
        issue.BlockedBy.Where(number => issues.All(candidate => candidate.Number != number)).ToList();

    public static bool IsBlocked(Issue issue, IReadOnlyCollection<Issue> issues) =>
        OpenBlockers(issue, issues).Count > 0 || MissingBlockers(issue, issues).Count > 0;

    public static IReadOnlyList<Issue> Blocks(Issue issue, IReadOnlyCollection<Issue> issues) =>
        issues.Where(candidate => candidate.BlockedBy.Contains(issue.Number)).ToList();

    public static bool CreatesCycle(IReadOnlyCollection<Issue> issues, int issueNumber, int blockerNumber)
    {
        var edges = Edges(issues);
        var visited = new HashSet<int>();
        var pending = new Stack<int>([blockerNumber]);
        while (pending.TryPop(out var current))
        {
            if (current == issueNumber)
                return true;
            if (!visited.Add(current))
                continue;
            foreach (var next in edges.GetValueOrDefault(current, []))
                pending.Push(next);
        }

        return false;
    }

    public static IReadOnlyList<IReadOnlyList<int>> FindCycles(IReadOnlyCollection<Issue> issues)
    {
        var edges = Edges(issues);
        var cycles = new List<IReadOnlyList<int>>();
        var seen = new HashSet<string>();
        var finished = new HashSet<int>();
        var path = new List<int>();

        void Visit(int number)
        {
            var index = path.IndexOf(number);
            if (index >= 0)
            {
                var cycle = path[index..];
                if (seen.Add(string.Join(',', cycle.Order())))
                    cycles.Add(cycle);
                return;
            }

            if (finished.Contains(number))
                return;

            path.Add(number);
            foreach (var next in edges.GetValueOrDefault(number, []))
                Visit(next);
            path.RemoveAt(path.Count - 1);
            finished.Add(number);
        }

        foreach (var number in edges.Keys.Order())
            Visit(number);

        return cycles;
    }

    static Dictionary<int, int[]> Edges(IReadOnlyCollection<Issue> issues) =>
        issues
            .GroupBy(issue => issue.Number)
            .ToDictionary(group => group.Key, group => group.SelectMany(issue => issue.BlockedBy).Distinct().ToArray());
}
