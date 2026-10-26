using RepoCli.Core.Features.Anchors;
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
        Assert.That(LocationInput.TryParse(text, out var location), Is.True);
        Assert.That(location, Is.EqualTo(new LocationInput(path, line, column)));
    }

    [TestCase("src/a.cs:12-20", "src/a.cs", 12, 20)]
    [TestCase(@"C:\src\a.cs:3-3", @"C:\src\a.cs", 3, 3)]
    public void ParsesLineRanges(string text, string path, int line, int endLine)
    {
        Assert.That(LocationInput.TryParse(text, out var location), Is.True);
        Assert.That(location, Is.EqualTo(new LocationInput(path, line, EndLine: endLine)));
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase(":12")]
    [TestCase(":12-14")]
    public void RejectsInvalid(string text) => Assert.That(LocationInput.TryParse(text, out _), Is.False);

    [TestCase("src/a.cs:12:5")]
    [TestCase("src/a.cs:12")]
    [TestCase("src/a.cs:12-14")]
    [TestCase("src/a.cs")]
    public void RoundTripsThroughToString(string text)
    {
        LocationInput.TryParse(text, out var location);
        Assert.That(location!.ToString(), Is.EqualTo(text));
    }

    [Test]
    public void MakesPathRelativeToRepoWithForwardSlashes()
    {
        var root = Path.GetTempPath();
        var location = new LocationInput(Path.Combine("src", "a.cs"), 1).RelativeTo(root, Path.Combine(root, "sub"));
        Assert.That(location.Path, Is.EqualTo("sub/src/a.cs"));
    }

    [Test]
    public void LegacyJsonLoadsWithoutAnchor()
    {
        var location = IssueJson.Deserialize<SourceLocation>("""{ "path": "src/a.cs", "line": 3, "column": 5 }""")!;

        Assert.That(location, Is.EqualTo(new SourceLocation("src/a.cs", 3, 5)));
        Assert.That(location.Anchor, Is.Null);
    }

    [Test]
    public void AnchoredJsonRoundTripsWithAnchorFieldsFirst()
    {
        var anchor = new Anchor("src/a.cs", 12, 3, "0123456789ab", "ba9876543210", "Calculator.Add");
        var location = SourceLocation.From(anchor, column: 9);

        var json = IssueJson.Serialize(location);

        Assert.That(json.ReplaceLineEndings("").Replace(" ", ""), Is.EqualTo(
            """{"path":"src/a.cs","line":12,"span":3,"symbol":"Calculator.Add","context":"0123456789ab","body":"ba9876543210","column":9}"""));
        var loaded = IssueJson.Deserialize<SourceLocation>(json)!;
        Assert.That(loaded, Is.EqualTo(location));
        Assert.That(loaded.Anchor, Is.EqualTo(anchor));
    }

    [Test]
    public void DescribeShowsSpanAndSymbol()
    {
        var anchor = new Anchor("src/a.cs", 12, 3, "c", "b", "Calculator.Add");

        Assert.That(SourceLocation.From(anchor).Describe(), Is.EqualTo("src/a.cs:12 (3 lines) in Calculator.Add"));
        Assert.That(SourceLocation.From(anchor with { Span = 1, Symbol = null }, 4).Describe(), Is.EqualTo("src/a.cs:12:4"));
        Assert.That(new SourceLocation("src").Describe(), Is.EqualTo("src"));
    }
}
