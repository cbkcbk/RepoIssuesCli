using System.Text.Json;
using RepoIssuesCli.Features.Issues;

namespace RepoIssuesCli.Tests.Cli;

public class LocationCommandTests
{
    const string Calculator = """
        namespace Demo;

        public class Calculator
        {
            public int Add(int a, int b)
            {
                var sum = a + b;
                return sum;
            }

            public int Negate(int a) => -a;
        }

        """;

    const string CalculatorPath = "src/Calculator.cs";
    const string HelperPath = "src/Helper.cs";

    const string Notes = """
        # Notes

        Intro paragraph about the tool.
        Setup requires dotnet ten.
        Run the build with binlog enabled.
        Then run all the tests.
        Finally review the output carefully.

        """;

    const string NotesPath = "docs/notes.md";

    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    string At(string path) => Path.Combine(repo.Root, path);

    Issue Single() => repo.Issues.Single();

    string IssueFile() => File.ReadAllText(repo.Store.PathFor(Single()));

    [Test]
    public void NewLocationGetsHashesSymbolAndColumn()
    {
        repo.Write(CalculatorPath, Calculator);

        var location = repo.Add("A", "-l", At(CalculatorPath) + ":7:13").Locations.Single();

        Assert.Multiple(() =>
        {
            Assert.That(location.Anchor, Is.Not.Null);
            Assert.That(location.Path, Is.EqualTo(CalculatorPath));
            Assert.That(location.Line, Is.EqualTo(7));
            Assert.That(location.Column, Is.EqualTo(13));
            Assert.That(location.Span, Is.EqualTo(1));
            Assert.That(location.Symbol, Is.EqualTo("Calculator.Add"));
            Assert.That(location.Context, Has.Length.EqualTo(12));
            Assert.That(location.Body, Has.Length.EqualTo(12));
            Assert.That(Single().Locations.Single(), Is.EqualTo(location));
        });
        Assert.That(IssueFile(), Does.Contain("\"context\"").And.Contain("\"body\"").And.Contain("\"symbol\": \"Calculator.Add\""));
    }

    [Test]
    public void LineRangeSetsTheSpan()
    {
        repo.Write(CalculatorPath, Calculator);

        var location = repo.Add("A", "-l", At(CalculatorPath) + ":5-9").Locations.Single();

        Assert.That((location.Line, location.Span, location.Symbol), Is.EqualTo((5, 5, "Calculator.Add")));
    }

    [Test]
    public void NonSourceFilesGetNoSymbol()
    {
        repo.Write(NotesPath, Notes);

        var location = repo.Add("A", "-l", At(NotesPath) + ":5").Locations.Single();

        Assert.That(location.Anchor, Is.Not.Null);
        Assert.That(location.Symbol, Is.Null);
    }

    [Test]
    public void EditAddLocationAnchorsAndRemoveLocationMatchesAnyColumn()
    {
        repo.Write(CalculatorPath, Calculator);
        repo.Add("A", "-l", At(CalculatorPath) + ":11:5");

        var added = repo.Run("edit", "1", "--add-location", At(CalculatorPath) + ":7", "--remove-location", At(CalculatorPath) + ":11");

        Assert.That(added.ExitCode, Is.Zero, added.Errors);
        var location = Single().Locations.Single();
        Assert.That((location.Line, location.Symbol, location.Anchor is not null), Is.EqualTo((7, "Calculator.Add", true)));
    }

    [Test]
    public void MissingFileOrLineStaysAPlainLocation()
    {
        repo.Write(CalculatorPath, Calculator);

        var issue = repo.Add("A", "-l", At("src/gone.cs") + ":3", "-l", At(CalculatorPath) + ":99");

        Assert.That(issue.Locations, Is.EqualTo(new[] { new SourceLocation("src/gone.cs", 3), new SourceLocation(CalculatorPath, 99) }));
    }

    [Test]
    public void LegacyLocationsLoadAndUpgradeOnSave()
    {
        repo.Write(CalculatorPath, Calculator);
        repo.Store.Initialize();
        repo.Write(".repo-issues/00001-legacy.json", """
            {
              "id": "01a14ac3-6d44-74e6-b11f-852a3f553cb2",
              "number": 1,
              "name": "Legacy",
              "locations": [
                { "path": "src/Calculator.cs", "line": 7, "column": 9 },
                { "path": "src/gone.cs", "line": 3 },
                { "path": "src" },
                { "path": "src/Calculator.cs", "line": 99 }
              ],
              "created": "2026-10-17T16:48:03+00:00",
              "updated": "2026-10-17T16:48:03+00:00"
            }
            """);

        Assert.That(Single().Locations.Select(location => location.Anchor), Is.All.Null);
        var result = repo.Run("status", "1", "InProgress", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var expected = new[]
        {
            new SourceLocation(CalculatorPath, 7, 9),
            new SourceLocation("src/gone.cs", 3),
            new SourceLocation("src"),
            new SourceLocation(CalculatorPath, 99),
        };
        var saved = Single().Locations;
        Assert.Multiple(() =>
        {
            Assert.That(saved[0] with { Span = null, Symbol = null, Context = null, Body = null }, Is.EqualTo(expected[0]));
            Assert.That(saved[0].Anchor, Is.Not.Null);
            Assert.That(saved[0].Symbol, Is.EqualTo("Calculator.Add"));
            Assert.That(saved.Skip(1), Is.EqualTo(expected.Skip(1)));
            Assert.That(IssueJson.Deserialize<Issue>(result.Output)!.Locations, Is.EqualTo(saved));
        });
    }

    [Test]
    public void ShowRendersAnchorWithSpanAndSymbol()
    {
        repo.Write(CalculatorPath, Calculator);
        repo.Add("A", "-l", At(CalculatorPath) + ":5-9", "-l", At(CalculatorPath) + ":11:5", "-l", At("src/gone.cs") + ":2");

        var output = repo.Run("show", "1").Output;

        Assert.That(output, Does.Contain("src/Calculator.cs:5 (5 lines) in Calculator.Add")
            .And.Contain("src/Calculator.cs:11:5 in Calculator.Negate")
            .And.Contain("src/gone.cs:2"));
    }

    [Test]
    public void JsonOutputKeepsLocationsAsAnArrayOfFlatAnchors()
    {
        repo.Write(CalculatorPath, Calculator);
        repo.Add("A", "-l", At(CalculatorPath) + ":7");

        var issue = JsonDocument.Parse(repo.Run("show", "1", "--json").Output).RootElement;

        var location = issue.GetProperty("locations").EnumerateArray().Single();
        Assert.That(location.EnumerateObject().Select(property => property.Name),
            Is.EqualTo(new[] { "path", "line", "span", "symbol", "context", "body" }));
    }

    void AddIssueWithThreeAnchors()
    {
        repo.Write(CalculatorPath, Calculator);
        repo.Write(HelperPath, "class Helper\n{\n}\n");
        repo.Add("A", "-l", At(CalculatorPath) + ":5-9", "-l", At(CalculatorPath) + ":11", "-l", At(HelperPath) + ":1");
    }

    void MoveChangeAndBreak()
    {
        repo.Write(CalculatorPath, "// header\n" + Calculator.Replace("=> -a;", "=> 0 - a;"));
        File.Delete(At(HelperPath));
    }

    static List<JsonElement> Items(RunResult result) => JsonDocument.Parse(result.Output).RootElement.EnumerateArray().ToList();

    static string Kind(JsonElement item) => item.GetProperty("kind").GetString()!;

    [Test]
    public void ValidateReportsMovedChangedAndBrokenAsWarnings()
    {
        AddIssueWithThreeAnchors();
        MoveChangeAndBreak();

        var json = repo.Run("validate", "--json");
        var text = repo.Run("validate");

        Assert.That(json.ExitCode, Is.Zero, json.Errors);
        var items = Items(json);
        Assert.Multiple(() =>
        {
            Assert.That(items.Select(Kind), Is.EqualTo(new[] { "LocationMoved", "LocationChanged", "LocationBroken" }));
            Assert.That(items.Select(item => item.GetProperty("severity").GetString()), Is.All.EqualTo("Warning"));
            Assert.That(items[0].GetProperty("location").GetString(), Is.EqualTo("src/Calculator.cs:5"));
            Assert.That(items[0].GetProperty("resolvedTo").GetString(), Is.EqualTo("src/Calculator.cs:6"));
            Assert.That(items[0].GetProperty("file").GetString(), Is.EqualTo("00001-a.json"));
            Assert.That(items.Any(item => item.TryGetProperty("fixed", out _) || item.TryGetProperty("needsConfirmation", out _)), Is.False);
        });
        Assert.That(text.ExitCode, Is.Zero, text.Errors);
        Assert.That(text.Output, Does.Contain("LocationMoved").And.Contain("3 warning(s) found."));
    }

    [Test]
    public void ValidateStrictFailsOnAnchorWarnings()
    {
        AddIssueWithThreeAnchors();
        Assert.That(repo.Run("validate", "--strict").ExitCode, Is.Zero);

        MoveChangeAndBreak();

        Assert.That(repo.Run("validate", "--strict").ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
    }

    [Test]
    public void FixRewritesMovedHintsOnlyAndKeepsUpdated()
    {
        AddIssueWithThreeAnchors();
        var before = Single();
        MoveChangeAndBreak();

        var result = repo.Run("validate", "--fix", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var items = Items(result);
        Assert.Multiple(() =>
        {
            Assert.That(items.Select(Kind), Is.EqualTo(new[] { "LocationMoved", "LocationChanged", "LocationBroken" }));
            Assert.That(items[0].GetProperty("fixed").GetBoolean(), Is.True);
            Assert.That(items[0].GetProperty("location").GetString(), Is.EqualTo("src/Calculator.cs:5"));
            Assert.That(items[0].GetProperty("resolvedTo").GetString(), Is.EqualTo("src/Calculator.cs:6"));
            Assert.That(items[0].GetProperty("severity").GetString(), Is.EqualTo("Warning"));
            Assert.That(items[1].TryGetProperty("fixed", out _), Is.False);
        });

        var after = Single();
        Assert.Multiple(() =>
        {
            Assert.That(after.Locations[0], Is.EqualTo(before.Locations[0] with { Line = 6 }));
            Assert.That(after.Locations.Skip(1), Is.EqualTo(before.Locations.Skip(1)));
            Assert.That(after.Updated, Is.EqualTo(before.Updated));
        });

        var again = Items(repo.Run("validate", "--json"));
        Assert.That(again.Select(Kind), Is.EqualTo(new[] { "LocationChanged", "LocationBroken" }));
    }

    [Test]
    public void FixPrintsWhatItFixed()
    {
        AddIssueWithThreeAnchors();
        MoveChangeAndBreak();

        var output = repo.Run("validate", "--fix").Output;

        Assert.That(output, Does.Contain("1 location hint(s) fixed.").And.Contain("src/Calculator.cs:6").And.Contain("2 warning(s) found."));
    }

    [Test]
    public void FixWithStrictPassesWhenOnlyMovedHintsWereStale()
    {
        repo.Write(CalculatorPath, Calculator);
        repo.Add("A", "-l", At(CalculatorPath) + ":7");
        repo.Write(CalculatorPath, "using System;\n\n" + Calculator);

        Assert.That(repo.Run("validate", "--strict").ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(repo.Run("validate", "--fix", "--strict").ExitCode, Is.Zero);
        Assert.That(Single().Locations.Single().Line, Is.EqualTo(9));
    }

    [Test]
    public void FixLeavesFuzzyMatchesForConfirmation()
    {
        repo.InitGit();
        repo.Write(NotesPath, Notes);
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "notes");
        repo.Add("A", "-l", At(NotesPath) + ":5");
        var before = IssueFile();
        repo.Write(NotesPath, "Preface.\n" + Notes.Replace("Setup requires dotnet ten.", "Setup requires dotnet ten or newer."));

        var result = repo.Run("validate", "--fix", "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var item = Items(result).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Kind(item), Is.EqualTo("LocationMoved"));
            Assert.That(item.GetProperty("needsConfirmation").GetBoolean(), Is.True);
            Assert.That(item.TryGetProperty("fixed", out _), Is.False);
            Assert.That(item.GetProperty("message").GetString(), Does.Contain("Needs confirmation"));
            Assert.That(IssueFile(), Is.EqualTo(before));
        });
    }
}
