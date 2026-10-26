using RepoCli.Core.Features.Anchors;

namespace RepoIssuesCli.Features.Issues;

public enum ProblemKind
{
    InvalidFile,
    DuplicateNumber,
    SelfBlock,
    DanglingBlocker,
    BlockerCycle,
    MissingLocationFile,
    LocationLineOutOfRange,
    LocationMoved,
    LocationChanged,
    LocationBroken,
}

/// <param name="Location">The location as stored in the issue, for location problems.</param>
/// <param name="ResolvedTo">Where a moved or changed anchor was found.</param>
/// <param name="NeedsConfirmation">True when the anchor was only found by similar context.</param>
/// <param name="Fixed">True when <c>validate --fix</c> rewrote the hint; the item is a record, not a problem.</param>
public sealed record ValidationProblem(
    ProblemKind Kind,
    string Message,
    string? File = null,
    string? Location = null,
    string? ResolvedTo = null,
    bool? NeedsConfirmation = null,
    bool? Fixed = null)
{
    public Severity Severity => Kind is ProblemKind.MissingLocationFile or ProblemKind.LocationLineOutOfRange
        or ProblemKind.LocationMoved or ProblemKind.LocationChanged or ProblemKind.LocationBroken
        ? Severity.Warning
        : Severity.Error;

    public static ValidationProblem From(LocationFix fix) => new(
        ProblemKind.LocationMoved,
        $"{fix.Issue}: updated {fix.From} to {fix.To} ({fix.Reason}).",
        fix.File,
        fix.From,
        fix.To,
        Fixed: true);
}

public static class IssueValidator
{
    public static IReadOnlyList<ValidationProblem> Validate(
        IssueSnapshot snapshot,
        Func<string, int?> lineCount,
        Func<Anchor, AnchorResolution> resolve)
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
            var file = IssueStore.FileNameFor(issue);
            if (issue.BlockedBy.Contains(issue.Number))
                problems.Add(new ValidationProblem(ProblemKind.SelfBlock, $"{issue.Title} blocks itself.", file));

            foreach (var missing in issue.BlockedBy.Where(number => !numbers.Contains(number)))
                problems.Add(new ValidationProblem(ProblemKind.DanglingBlocker, $"{issue.Title} is blocked by {missing:D4}, which does not exist.", file));

            foreach (var location in issue.Locations)
            {
                var problem = location.Anchor is { } anchor
                    ? AnchorProblem(issue, location, resolve(anchor))
                    : PlainProblem(issue, location, lineCount(location.Path));
                if (problem is not null)
                    problems.Add(problem with { File = file });
            }
        }

        problems.AddRange(BlockerGraph.FindCycles(issues)
            .Where(cycle => cycle.Count > 1)
            .Select(cycle => new ValidationProblem(
                ProblemKind.BlockerCycle,
                $"Blocker cycle: {string.Join(" -> ", cycle.Append(cycle[0]).Select(number => number.ToString("D4")))}.")));

        return problems;
    }

    static ValidationProblem? PlainProblem(Issue issue, SourceLocation location, int? lines)
    {
        if (lines is null)
            return new ValidationProblem(ProblemKind.MissingLocationFile, $"{issue.Title} points at {location}, which does not exist.", Location: location.ToString());

        return location.Line > lines
            ? new ValidationProblem(ProblemKind.LocationLineOutOfRange, $"{issue.Title} points at {location}, but {location.Path} has {lines} lines.", Location: location.ToString())
            : null;
    }

    static ValidationProblem? AnchorProblem(Issue issue, SourceLocation location, AnchorResolution resolution)
    {
        var found = resolution.Resolved is { } resolved ? $"{resolved.Path}:{resolved.Line}" : null;
        var confirm = resolution.NeedsConfirmation ? " Needs confirmation: only similar context was found; check it and re-add the location." : "";
        var (kind, message) = resolution.State switch
        {
            AnchorState.Ok => (default(ProblemKind?), ""),
            AnchorState.Moved when resolution.NeedsConfirmation => (ProblemKind.LocationMoved, $"{issue.Title} points at {location.Describe()}, which may have moved to {found} ({resolution.Reason})."),
            AnchorState.Moved => (ProblemKind.LocationMoved, $"{issue.Title} points at {location.Describe()}, which moved to {found} ({resolution.Reason}). Run 'issues validate --fix' to update it."),
            AnchorState.Changed => (ProblemKind.LocationChanged, $"{issue.Title} points at {location.Describe()}, whose code changed ({resolution.Reason})."),
            _ => (ProblemKind.LocationBroken, $"{issue.Title} points at {location.Describe()}, which was not found ({resolution.Reason})."),
        };

        return kind is { } problemKind
            ? new ValidationProblem(problemKind, message + confirm, Location: location.ToString(), ResolvedTo: found,
                NeedsConfirmation: resolution.NeedsConfirmation ? true : null)
            : null;
    }
}
