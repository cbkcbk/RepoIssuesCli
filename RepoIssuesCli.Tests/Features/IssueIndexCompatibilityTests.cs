using RepoCli.Core.Features.IssueLinks;
using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Features;

public class IssueIndexCompatibilityTests
{
    [Test]
    public void CoreIndexReadsFilesSavedByTheIssueStore()
    {
        using var repo = new TempRepo();
        repo.Store.Initialize();
        var store = repo.Store;

        void Save(int number, IssueStatus status, params int[] blockedBy) => store.Save(new Issue
        {
            Id = Guid.CreateVersion7(),
            Number = number,
            Name = $"Issue {number}",
            Status = status,
            BlockedBy = blockedBy,
            Tags = ["docs"],
            Created = Issue.Timestamp(),
            Updated = Issue.Timestamp(),
        });

        Save(1, IssueStatus.Done);
        Save(2, IssueStatus.InProgress, 1, 9);
        Save(3, IssueStatus.Cancelled);

        var index = IssueIndex.Load(repo.Root);

        Assert.That(index.Errors, Is.Empty);
        Assert.That(index.Issues.Select(issue => (issue.Number, issue.Name, issue.State)),
            Is.EqualTo(new[] { (1, "Issue 1", IssueState.Done), (2, "Issue 2", IssueState.InProgress), (3, "Issue 3", IssueState.Cancelled) }));
        Assert.That(index.Find(2)!.BlockedBy, Is.EqualTo(new[] { 1, 9 }));
        Assert.That(index.UnresolvedBlockers(index.Find(2)!), Is.EqualTo(new[] { 9 }));
    }

    [Test]
    public void CoreStatesMatchIssueStatuses()
    {
        Assert.That(Enum.GetNames<IssueState>(), Is.EqualTo(Enum.GetNames<IssueStatus>()));
    }
}
