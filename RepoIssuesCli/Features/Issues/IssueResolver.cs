namespace RepoIssuesCli.Features.Issues;

public sealed class IssueNotFoundException(string reference)
    : Exception($"No issue matches '{reference}'.");

public sealed class AmbiguousIssueReferenceException(string reference, IEnumerable<Issue> candidates)
    : Exception($"'{reference}' matches several issues: {string.Join(", ", candidates.Select(issue => $"{issue.Title} ({issue.Id})"))}. Use the id to pick one.");

public static class IssueResolver
{
    public static Issue Resolve(IReadOnlyCollection<Issue> issues, string reference) =>
        Single(reference, FindAll(issues, reference));

    public static IReadOnlyList<Issue> FindAll(IReadOnlyCollection<Issue> issues, string reference)
    {
        var text = reference.Trim();

        if (int.TryParse(text, out var number))
            return issues.Where(issue => issue.Number == number).ToList();

        if (Guid.TryParse(text, out var id))
            return issues.Where(issue => issue.Id == id).ToList();

        var exact = issues.Where(issue => NameMatches(issue, text, exactly: true)).ToList();
        return exact.Count > 0 ? exact : issues.Where(issue => NameMatches(issue, text, exactly: false)).ToList();
    }

    public static int ResolveNumber(IReadOnlyCollection<Issue> issues, string reference) =>
        int.TryParse(reference.Trim(), out var number) ? number : Resolve(issues, reference).Number;

    public static Issue Single(string reference, IReadOnlyList<Issue> matches) => matches.Count switch
    {
        1 => matches[0],
        0 => throw new IssueNotFoundException(reference),
        _ => throw new AmbiguousIssueReferenceException(reference, matches),
    };

    static bool NameMatches(Issue issue, string text, bool exactly) => exactly
        ? issue.Name.Equals(text, StringComparison.OrdinalIgnoreCase) || issue.Title.Equals(text, StringComparison.OrdinalIgnoreCase)
        : issue.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) || issue.Title.StartsWith(text, StringComparison.OrdinalIgnoreCase);
}
