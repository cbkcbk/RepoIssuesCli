using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Cli;

public static class IssueInteraction
{
    extension(Interaction interaction)
    {
        public Issue ChooseIssue(IReadOnlyCollection<Issue> issues, string? reference, string title)
        {
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var matches = IssueResolver.FindAll(issues, reference);
                return matches.Count > 1 && interaction.IsInteractive
                    ? interaction.Choose(title, matches, issue => $"{issue.Title} ({issue.Id})", "<ref>")
                    : IssueResolver.Single(reference, matches);
            }

            interaction.EnsureInteractive("<ref>");
            if (issues.Count == 0)
                throw new UsageException("There are no issues yet.");

            var ordered = issues.OrderBy(issue => issue.Status.IsClosed()).ThenBy(issue => issue.Number);
            return interaction.Choose(title, ordered, issue => $"{issue.Title} [{issue.Status}]", "<ref>");
        }
    }
}