using RepoCli.Core.Features.Anchors;

namespace RepoCli.Core.Tests.Features.Anchors;

public class AnchorResolverTests
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
    const int AddLine = 5;
    const int AddEndLine = 9;
    const int SumLine = 7;

    const string Notes = """
        # Notes

        Intro paragraph about the tool.
        Setup requires dotnet ten.
        Run the build with binlog enabled.
        Then run all the tests.
        Finally review the output carefully.

        """;

    const string NotesPath = "docs/notes.md";
    const int BuildLine = 5;

    TempFolder folder = null!;

    [SetUp]
    public void SetUp() => folder = new TempFolder();

    [TearDown]
    public void TearDown() => folder.Dispose();

    AnchorResolver Resolver => new(folder.Root);

    string FullPath(string path) => Path.Combine(folder.Root, path);

    void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FullPath(path))!);
        File.WriteAllText(FullPath(path), content);
    }

    void Commit(string message = "commit")
    {
        folder.Git("add", "-A");
        folder.Git("commit", "-q", "-m", message);
    }

    Anchor AddAnchor() => Anchor.Load(folder.Root, CalculatorPath, AddLine, AddEndLine);

    Anchor SumAnchorWithSymbol() =>
        Anchor.CreateWithSymbol(CalculatorPath, SourceText.Load(FullPath(CalculatorPath)), SumLine);

    [Test]
    public void UnchangedFileIsOk()
    {
        Write(CalculatorPath, Calculator);
        var anchor = AddAnchor();

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Ok));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Hint));
            Assert.That(result.Resolved, Is.EqualTo(anchor));
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    [Test]
    public void LinesInsertedAboveAreMovedAndFixed()
    {
        Write(CalculatorPath, Calculator);
        var anchor = AddAnchor();
        Write(CalculatorPath, "using System;\nusing System.Linq;\n\n" + Calculator);

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.SameFile));
            Assert.That(result.NeedsConfirmation, Is.False);
            Assert.That(AnchorResolver.Fix(result), Is.EqualTo(anchor with { Line = AddLine + 3 }));
        });
    }

    [Test]
    public void EditedBodyIsChangedAndNotFixed()
    {
        Write(CalculatorPath, Calculator);
        var anchor = AddAnchor();
        Write(CalculatorPath, Calculator.Replace("var sum = a + b;", "var sum = checked(a + b);"));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Changed));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Hint));
            Assert.That(result.Resolved?.Line, Is.EqualTo(AddLine));
            Assert.That(result.Resolved?.Body, Is.Not.EqualTo(anchor.Body));
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    const string Repeated = "one\ntwo\nthree\nalpha\none\ntwo\nthree\nbeta\n";

    [Test]
    public void DuplicatedContextPicksTheNearestMatch()
    {
        Write("list.txt", Repeated.Replace("alpha", "beta"));
        var anchor = Anchor.Load(folder.Root, "list.txt", 7, 8);
        Write("list.txt", "new\n" + Repeated.Replace("alpha", "beta"));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.Resolved?.Line, Is.EqualTo(8));
        });
    }

    [Test]
    public void DuplicatedContextPrefersTheMatchingBodyOverTheNearest()
    {
        Write("list.txt", Repeated);
        var anchor = Anchor.Load(folder.Root, "list.txt", 7, 8);
        Write("list.txt", "new\nnew\nnew\n" + Repeated);

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(AnchorResolver.Fix(result)?.Line, Is.EqualTo(10));
        });
    }

    [Test]
    public void DuplicatedContextWithoutMatchingBodyIsChangedAtTheNearest()
    {
        Write("list.txt", Repeated);
        var anchor = Anchor.Load(folder.Root, "list.txt", 7, 8);
        Write("list.txt", "new\nnew\nnew\n" + Repeated.Replace("alpha", "gamma").Replace("beta", "delta"));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Changed));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.SameFile));
            Assert.That(result.Resolved?.Line, Is.EqualTo(6));
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    [Test]
    public void CommittedRenameThenMovedLineIsMovedViaRename()
    {
        folder.InitGit();
        Write(CalculatorPath, Calculator);
        Commit();
        var anchor = AddAnchor();
        folder.Git("mv", CalculatorPath, "src/Arithmetic.cs");
        Commit("rename");
        Write("src/Arithmetic.cs", "// moved\n\n" + Calculator);

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Rename));
            Assert.That(AnchorResolver.Fix(result), Is.EqualTo(anchor with { Path = "src/Arithmetic.cs", Line = AddLine + 2 }));
        });
    }

    [Test]
    public void RenamesAreFollowedAcrossSeveralCommits()
    {
        folder.InitGit();
        Write(CalculatorPath, Calculator);
        Commit();
        var anchor = AddAnchor();
        folder.Git("mv", CalculatorPath, "src/Arithmetic.cs");
        Commit("first rename");
        folder.Git("mv", "src/Arithmetic.cs", "src/Maths.cs");
        Commit("second rename");

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.Resolved?.Path, Is.EqualTo("src/Maths.cs"));
            Assert.That(result.Resolved?.Line, Is.EqualTo(AddLine));
        });
    }

    [Test]
    public void UncommittedGitMoveIsMovedViaRename()
    {
        folder.InitGit();
        Write(CalculatorPath, Calculator);
        Commit();
        var anchor = AddAnchor();
        folder.Git("mv", CalculatorPath, "src/Arithmetic.cs");
        Write("src/Arithmetic.cs", "// moved\n" + Calculator);

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Rename));
            Assert.That(AnchorResolver.Fix(result), Is.EqualTo(anchor with { Path = "src/Arithmetic.cs", Line = AddLine + 1 }));
        });
    }

    [Test]
    public void RenamedFileWithEditedBodyIsChanged()
    {
        folder.InitGit();
        Write(CalculatorPath, Calculator);
        Commit();
        var anchor = AddAnchor();
        folder.Git("mv", CalculatorPath, "src/Arithmetic.cs");
        Write("src/Arithmetic.cs", Calculator.Replace("return sum;", "return sum + 0;"));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Changed));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Rename));
            Assert.That(result.Resolved?.Path, Is.EqualTo("src/Arithmetic.cs"));
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    const string EditedHeader = "public int Add(int a, int b, int unused = 0)";

    [Test]
    public void EditedContextAboveIsMovedViaSymbol()
    {
        Write(CalculatorPath, Calculator);
        var anchor = SumAnchorWithSymbol();
        Write(CalculatorPath, "using System;\n\n" + Calculator.Replace("public int Add(int a, int b)", EditedHeader));

        var result = Resolver.Resolve(anchor);
        var fixedAnchor = AnchorResolver.Fix(result);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Symbol, Is.EqualTo("Calculator.Add"));
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Symbol));
            Assert.That(fixedAnchor?.Line, Is.EqualTo(SumLine + 2));
            Assert.That(fixedAnchor?.Body, Is.EqualTo(anchor.Body));
            Assert.That(fixedAnchor?.Context, Is.Not.EqualTo(anchor.Context));
            Assert.That(fixedAnchor?.Symbol, Is.EqualTo("Calculator.Add"));
        });

        Assert.That(Resolver.Resolve(fixedAnchor!).State, Is.EqualTo(AnchorState.Ok));
    }

    [Test]
    public void EditedContextAndBodyIsChangedViaSymbol()
    {
        Write(CalculatorPath, Calculator);
        var anchor = SumAnchorWithSymbol();
        Write(CalculatorPath, Calculator.Replace("public int Add(int a, int b)", EditedHeader).Replace("a + b;", "b + a;"));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Changed));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Symbol));
            Assert.That(result.Resolved?.Line, Is.EqualTo(AddLine));
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    [Test]
    public void SymbolAndOffsetAreRecoveredFromGitHistory()
    {
        folder.InitGit();
        Write(CalculatorPath, Calculator);
        Commit();
        var anchor = Anchor.Load(folder.Root, CalculatorPath, SumLine);
        Write(CalculatorPath, "using System;\n\n" + Calculator.Replace("public int Add(int a, int b)", EditedHeader).Replace("a + b;", "b + a;"));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(anchor.Symbol, Is.Null);
            Assert.That(result.State, Is.EqualTo(AnchorState.Changed));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Symbol));
            Assert.That(result.Resolved?.Line, Is.EqualTo(SumLine + 2));
        });
    }

    [Test]
    public void SlightlyEditedContextIsFuzzyAndNeedsConfirmation()
    {
        folder.InitGit();
        Write(NotesPath, Notes);
        Commit();
        var anchor = Anchor.Load(folder.Root, NotesPath, BuildLine);
        Write(NotesPath, "Preface.\n" + Notes.Replace("Setup requires dotnet ten.", "Setup requires dotnet ten or newer."));

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Moved));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.Fuzzy));
            Assert.That(result.NeedsConfirmation, Is.True);
            Assert.That(result.Similarity, Is.GreaterThanOrEqualTo(0.7).And.LessThan(1));
            Assert.That(result.Resolved?.Line, Is.EqualTo(BuildLine + 1));
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    [Test]
    public void RewrittenTextIsBroken()
    {
        folder.InitGit();
        Write(NotesPath, Notes);
        Commit();
        var anchor = Anchor.Load(folder.Root, NotesPath, BuildLine);
        Write(NotesPath, "# Something else\n\nCompletely different words here.\nNothing in common at all.\n");

        Assert.That(Resolver.Resolve(anchor).State, Is.EqualTo(AnchorState.Broken));
    }

    [Test]
    public void DeletedFileIsBroken()
    {
        folder.InitGit();
        Write(CalculatorPath, Calculator);
        Commit();
        var anchor = AddAnchor();
        folder.Git("rm", "-q", CalculatorPath);
        Commit("delete");

        var result = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.State, Is.EqualTo(AnchorState.Broken));
            Assert.That(result.FoundBy, Is.EqualTo(AnchorSearch.None));
            Assert.That(result.Resolved, Is.Null);
            Assert.That(AnchorResolver.Fix(result), Is.Null);
        });
    }

    [Test]
    public void OutsideGitRenamesAndFuzzyAreSkippedWithoutThrowing()
    {
        Write(CalculatorPath, Calculator);
        Write(NotesPath, Notes);
        var renamed = AddAnchor();
        var edited = Anchor.Load(folder.Root, NotesPath, BuildLine);
        File.Move(FullPath(CalculatorPath), FullPath("src/Arithmetic.cs"));
        Write(NotesPath, Notes.Replace("Setup requires dotnet ten.", "Setup requires dotnet ten or newer."));

        var results = Resolver.ResolveAll([renamed, edited]);

        Assert.That(results.Select(result => result.State), Is.EqualTo(new[] { AnchorState.Broken, AnchorState.Broken }));
    }

    [Test]
    public void BatchReadsEachFileOnce()
    {
        Write(CalculatorPath, Calculator);
        var anchor = AddAnchor();
        var batch = Resolver.Batch();

        var before = batch.Resolve(anchor);
        Write(CalculatorPath, "// header\n" + Calculator);
        var cached = batch.Resolve(anchor);
        var fresh = Resolver.Resolve(anchor);

        Assert.Multiple(() =>
        {
            Assert.That(before.State, Is.EqualTo(AnchorState.Ok));
            Assert.That(cached.State, Is.EqualTo(AnchorState.Ok));
            Assert.That(fresh.State, Is.EqualTo(AnchorState.Moved));
        });
    }

    [Test]
    public void ResolveAllKeepsInputOrder()
    {
        Write(CalculatorPath, Calculator);
        var add = AddAnchor();
        var negate = Anchor.CreateWithSymbol(CalculatorPath, SourceText.Load(FullPath(CalculatorPath)), 11);
        Write(CalculatorPath, "// header\n" + Calculator.Replace("=> -a;", "=> 0 - a;"));

        var results = Resolver.ResolveAll([add, negate, add with { Path = "missing.cs" }]);

        Assert.That(results.Select(result => (result.Anchor, result.State)), Is.EqualTo(new[]
        {
            (add, AnchorState.Moved),
            (negate, AnchorState.Changed),
            (add with { Path = "missing.cs" }, AnchorState.Broken)
        }));
    }
}
