using System.Text.Json;
using RepoCli.Core.Features.Anchors;

namespace RepoCli.Core.Tests.Features.Anchors;

public class AnchorTests
{
    const string Original = """
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

    const int AddLine = 5;
    const int AddEndLine = 9;

    static SourceText Text(string text) => SourceText.Parse(text);

    static Anchor AddAnchor(string text, int line = AddLine, int endLine = AddEndLine) =>
        Anchor.Create("src/Calculator.cs", Text(text), line, endLine);

    [Test]
    public void WhitespaceOnlyReformattingKeepsBothHashes()
    {
        const string reformatted = """
            namespace   Demo;
            public class    Calculator
            {
              public int Add(int a,   int b)
              {
                var sum	=   a + b;
                  return sum;
              }
              public int Negate(int a) => -a;
            }
            """;
        var original = AddAnchor(Original);
        var moved = AddAnchor(reformatted, line: 4, endLine: 8);

        Assert.Multiple(() =>
        {
            Assert.That(moved.Context, Is.EqualTo(original.Context));
            Assert.That(moved.Body, Is.EqualTo(original.Body));
            Assert.That(moved.Span, Is.EqualTo(original.Span));
        });
    }

    [Test]
    public void EditedBodyChangesBodyHashButNotContextHash()
    {
        var original = AddAnchor(Original);
        var edited = AddAnchor(Original.Replace("var sum = a + b;", "var sum = checked(a + b);"));

        Assert.Multiple(() =>
        {
            Assert.That(edited.Context, Is.EqualTo(original.Context));
            Assert.That(edited.Body, Is.Not.EqualTo(original.Body));
        });
    }

    [Test]
    public void EditedStartLineChangesContextHash()
    {
        var original = AddAnchor(Original);
        var edited = AddAnchor(Original.Replace("public int Add(int a, int b)", "public long Add(long a, long b)"));

        Assert.That(edited.Context, Is.Not.EqualTo(original.Context));
    }

    [Test]
    public void InsertedLinesAboveShiftTheLineButContextStillMatchesThere()
    {
        var anchor = AddAnchor(Original);
        var shifted = Text("// header\n// license\nusing System;\n\n" + Original);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.ContextMatches(shifted, AddLine), Is.False);
            Assert.That(anchor.ContextMatches(shifted, AddLine + 4), Is.True);
            Assert.That(anchor.BodyMatches(shifted, AddLine + 4), Is.True);
        });
    }

    [Test]
    public void BlankLinesAreIgnoredInContextAndBody()
    {
        const string spaced = """
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
        var original = AddAnchor(Original);
        var anchor = AddAnchor(spaced, line: 8, endLine: 14);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Context, Is.EqualTo(original.Context));
            Assert.That(anchor.Body, Is.EqualTo(original.Body));
            Assert.That(anchor.Span, Is.EqualTo(5));
            Assert.That(original.BodyMatches(Text(spaced), 8), Is.True);
            Assert.That(Text(spaced).EndLine(8, original.Span), Is.EqualTo(14));
        });
    }

    [Test]
    public void BlankStartLineSnapsToNextContentLine()
    {
        var anchor = AddAnchor(Original, line: 2, endLine: 2);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Line, Is.EqualTo(3));
            Assert.That(anchor.Span, Is.EqualTo(1));
            Assert.That(anchor.ContextMatches(Text(Original), 3), Is.True);
        });
    }

    [Test]
    public void TrailingBlankLinesSnapBackToLastContentLine()
    {
        var text = Text("first\nlast\n\n\n");
        var anchor = Anchor.Create("a.txt", text, 4);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Line, Is.EqualTo(2));
            Assert.That(anchor.Span, Is.EqualTo(1));
            Assert.That(anchor.BodyMatches(text, 2), Is.True);
        });
    }

    [Test]
    public void SpanAtStartOfFile()
    {
        var anchor = AddAnchor(Original, line: 1, endLine: 3);
        var appended = Text(Original + "\n\n// trailing comment\n");

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Line, Is.EqualTo(1));
            Assert.That(anchor.Span, Is.EqualTo(2));
            Assert.That(anchor.ContextMatches(appended, 1), Is.True);
            Assert.That(anchor.BodyMatches(appended, 1), Is.True);
            Assert.That(anchor.EndLine(appended), Is.EqualTo(3));
        });
    }

    [Test]
    public void SpanAtEndOfFile()
    {
        var text = Text(Original);
        var anchor = AddAnchor(Original, line: 11, endLine: text.LineCount);
        var withNewline = Text(Original + "\n");

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Span, Is.EqualTo(2));
            Assert.That(withNewline.LineCount, Is.EqualTo(text.LineCount));
            Assert.That(anchor.ContextMatches(withNewline, 11), Is.True);
            Assert.That(anchor.BodyMatches(withNewline, 11), Is.True);
            Assert.That(anchor.EndLine(text), Is.EqualTo(text.LineCount));
        });
    }

    [Test]
    public void BodyCutShortByEndOfFileDoesNotMatch()
    {
        var anchor = AddAnchor(Original, line: 11, endLine: 12);
        var truncated = Text(Original[..Original.LastIndexOf('}')]);

        Assert.That(anchor.BodyMatches(truncated, 11), Is.False);
    }

    [Test]
    public void LineEndingsDoNotAffectHashes()
    {
        var unix = AddAnchor(Original.ReplaceLineEndings("\n"));
        var windows = AddAnchor(Original.ReplaceLineEndings("\r\n"));

        Assert.That(windows, Is.EqualTo(unix));
    }

    [TestCase(0, null)]
    [TestCase(13, null)]
    [TestCase(5, 4)]
    [TestCase(5, 13)]
    public void OutOfRangeLinesThrow(int line, int? endLine) =>
        Assert.That(() => Anchor.Create("a.cs", Text(Original), line, endLine), Throws.InstanceOf<ArgumentOutOfRangeException>());

    [Test]
    public void HashesAreShortLowercaseHex()
    {
        var anchor = AddAnchor(Original);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Context, Does.Match($"^[0-9a-f]{{{Fingerprint.HashLength}}}$"));
            Assert.That(anchor.Body, Does.Match($"^[0-9a-f]{{{Fingerprint.HashLength}}}$"));
        });
    }

    [TestCase("  a  b\t\tc  ", "a b c")]
    [TestCase("\t", "")]
    [TestCase("x", "x")]
    public void NormalizeTrimsAndCollapsesWhitespace(string line, string expected) =>
        Assert.That(Fingerprint.Normalize(line), Is.EqualTo(expected));

    [Test]
    public void LoadReadsFileRelativeToRootAndUsesForwardSlashes()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Subfolder("src"), "Calculator.cs"), Original);

        var anchor = Anchor.Load(folder.Root, @"src\Calculator.cs", AddLine, AddEndLine, symbol: "Calculator.Add");

        Assert.That(anchor, Is.EqualTo(AddAnchor(Original) with { Symbol = "Calculator.Add" }));
    }

    [Test]
    public void RoundTripsThroughJson()
    {
        var anchor = AddAnchor(Original) with { Symbol = "Calculator.Add" };

        var json = JsonSerializer.Serialize(anchor, JsonSerializerOptions.Web);

        Assert.That(JsonSerializer.Deserialize<Anchor>(json, JsonSerializerOptions.Web), Is.EqualTo(anchor));
    }
}
