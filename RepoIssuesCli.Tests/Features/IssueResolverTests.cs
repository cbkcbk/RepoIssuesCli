using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Features;

public class IssueResolverTests
{
    static readonly Issue[] Issues =
    [
        Make(1, "Login fails"),
        Make(2, "Login page slow"),
        Make(3, "Logout"),
    ];

    static Issue Make(int number, string name) => new()
    {
        Id = Guid.CreateVersion7(),
        Number = number,
        Name = name,
        Created = Issue.Timestamp(),
        Updated = Issue.Timestamp(),
    };

    [TestCase("2", 2)]
    [TestCase("0002", 2)]
    [TestCase("logout", 3)]
    [TestCase("Login fails", 1)]
    [TestCase("0001 Login fails", 1)]
    [TestCase("Login p", 2)]
    [TestCase("0003", 3)]
    public void ResolvesReference(string reference, int expected) =>
        Assert.That(IssueResolver.Resolve(Issues, reference).Number, Is.EqualTo(expected));

    [Test]
    public void ResolvesById() =>
        Assert.That(IssueResolver.Resolve(Issues, Issues[1].Id.ToString()), Is.SameAs(Issues[1]));

    [Test]
    public void AmbiguousPrefixListsCandidates()
    {
        var exception = Assert.Throws<AmbiguousIssueReferenceException>(() => IssueResolver.Resolve(Issues, "Log"));
        Assert.That(exception!.Message, Does.Contain("0001 Login fails").And.Contain("0003 Logout"));
    }

    [TestCase("9")]
    [TestCase("Nope")]
    public void UnknownReferenceThrowsNotFound(string reference) =>
        Assert.Throws<IssueNotFoundException>(() => IssueResolver.Resolve(Issues, reference));
}
