namespace RepoIssuesCli.Features.Issues;

public sealed class IssueNotFoundException(string reference)
    : Exception($"No issue matches '{reference}'.");

public sealed class AmbiguousIssueReferenceException(string reference, IEnumerable<Issue> candidates)
    : Exception($"'{reference}' matches several issues: {string.Join(", ", candidates.Select(issue => issue.Title))}.");

public static class IssueResolver
{
    public static Issue Resolve(IReadOnlyCollection<Issue> issues, string reference)
    {
        var text = reference.Trim();

        if (int.TryParse(text, out var number))
            return Single(reference, issues.Where(issue => issue.Number == number));

        if (Guid.TryParse(text, out var id))
            return Single(reference, issues.Where(issue => issue.Id == id));

        var exact = issues.Where(issue => Matches(issue, text, exactly: true)).ToList();
        if (exact.Count > 0)
            return Single(reference, exact);

        return Single(reference, issues.Where(issue => Matches(issue, text, exactly: false)));
    }

    public static int ResolveNumber(IReadOnlyCollection<Issue> issues, string reference) =>
        int.TryParse(reference.Trim(), out var number) ? number : Resolve(issues, reference).Number;

    static bool Matches(Issue issue, string text, bool exactly) => exactly
        ? issue.Name.Equals(text, StringComparison.OrdinalIgnoreCase) || issue.Title.Equals(text, StringComparison.OrdinalIgnoreCase)
        : issue.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) || issue.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase);

    static Issue Single(string reference, IEnumerable<Issue> matches)
    {
        var list = matches.ToList();
        return list.Count switch
        {
            1 => list[0],
            0 => throw new IssueNotFoundException(reference),
            _ => throw new AmbiguousIssueReferenceException(reference, list),
        };
    }
}
