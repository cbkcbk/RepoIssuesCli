using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Features;

public class SourceLocationTests
{
    [TestCase("src/a.cs", "src/a.cs", null, null)]
    [TestCase("src/a.cs:12", "src/a.cs", 12, null)]
    [TestCase("src/a.cs:12:5", "src/a.cs", 12, 5)]
    [TestCase(@"C:\src\a.cs:12:5", @"C:\src\a.cs", 12, 5)]
    [TestCase(@"C:\src\a.cs", @"C:\src\a.cs", null, null)]
    public void Parses(string text, string path, int? line, int? column)
    {
        Assert.That(SourceLocation.TryParse(text, out var location), Is.True);
        Assert.That(location, Is.EqualTo(new SourceLocation(path, line, column)));
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase(":12")]
    public void RejectsInvalid(string text) => Assert.That(SourceLocation.TryParse(text, out _), Is.False);

    [TestCase("src/a.cs:12:5")]
    [TestCase("src/a.cs:12")]
    [TestCase("src/a.cs")]
    public void RoundTripsThroughToString(string text)
    {
        SourceLocation.TryParse(text, out var location);
        Assert.That(location!.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void MakesPathRelativeToRepoWithForwardSlashes()
    {
        var root = Path.GetTempPath();
        var location = new SourceLocation(Path.Combine("src", "a.cs"), 1).RelativeTo(root, Path.Combine(root, "sub"));
        Assert.That(location.Path, Is.EqualTo("sub/src/a.cs"));
    }
}
