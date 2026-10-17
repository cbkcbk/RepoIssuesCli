namespace RepoIssuesCli.Features.Issues;

public sealed record RenumberResult
{
    public required Issue Issue { get; init; }
    public required int PreviousNumber { get; init; }
    public IReadOnlyList<Issue> Rewritten { get; set; } = [];
    public IReadOnlyList<Issue> KeptReferences { get; set; } = [];
}

public static class IssueRenumbering
{
    public static RenumberResult Renumber(IssueSnapshot snapshot, Issue issue)
    {
        var previous = issue.Number;
        var next = snapshot.NextNumber();
        var renumbered = issue with { Number = next, Updated = Issue.Timestamp() };
        var others = snapshot.Issues.Where(other => other.Id != issue.Id).ToList();

        if (others.Any(other => other.Number == previous))
        {
            return new RenumberResult
            {
                Issue = renumbered,
                PreviousNumber = previous,
                KeptReferences = others.Append(renumbered).Where(candidate => candidate.BlockedBy.Contains(previous)).ToList(),
            };
        }

        Issue Retarget(Issue candidate) =>
            candidate with { BlockedBy = candidate.BlockedBy.Select(number => number == previous ? next : number).ToList() };

        return new RenumberResult
        {
            Issue = Retarget(renumbered),
            PreviousNumber = previous,
            Rewritten = others.Where(other => other.BlockedBy.Contains(previous)).Select(Retarget).ToList(),
        };
    }
}
