using System.Text.Json.Serialization;
using RepoCli.Core.Features.IssueLinks;

namespace RepoIssuesCli.Features.Issues;

public sealed record RenumberResult
{
    public required Issue Issue { get; init; }
    public required int PreviousNumber { get; init; }
    public IReadOnlyList<Issue> Rewritten { get; set; } = [];
    public IReadOnlyList<Issue> KeptReferences { get; set; } = [];
    public IReadOnlyList<DocsReference> DocsReferences { get; set; } = [];
    public IReadOnlyList<DocsReference> KeptDocsReferences { get; set; } = [];

    [JsonIgnore]
    public bool NumberStillUsed { get; init; }
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
                NumberStillUsed = true,
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

    /// <remarks>The caller holds both the issues lock and <see cref="DocsSteps.Lock"/>.</remarks>
    public static (RenumberResult Result, DocsReferenceScan Scan) UpdateDocs(RenumberResult result, DocsSteps docs)
    {
        if (result.NumberStillUsed)
        {
            var kept = docs.Find(result.PreviousNumber);
            return (result with { KeptDocsReferences = kept.References }, kept);
        }

        var rewritten = docs.Renumber(result.PreviousNumber, result.Issue.Number);
        return (result with { DocsReferences = rewritten.References }, rewritten);
    }
}
