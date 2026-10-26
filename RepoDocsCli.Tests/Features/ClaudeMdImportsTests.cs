using RepoCli.Core.Features.Text;
using RepoDocsCli.Features.Budget;

namespace RepoDocsCli.Tests.Features;

public class ClaudeMdImportsTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(repo.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    ImportChain Follow(string text)
    {
        WriteFile("CLAUDE.md", text);
        return ClaudeMdImports.Follow(repo.Root, Path.Combine(repo.Root, "CLAUDE.md"), text);
    }

    [Test]
    public void FollowsNestedImportsRelativeToTheImportingFile()
    {
        WriteFile("docs/style.md", "Style rules. See @more/naming.md for names.");
        WriteFile("docs/more/naming.md", "PascalCase everywhere.");

        var chain = Follow("# Rules\n\n@docs/style.md\n");

        Assert.That(chain.Files, Is.EqualTo(new[]
        {
            new ImportedFile("docs/style.md", TokenEstimate.Of("Style rules. See @more/naming.md for names."), 1, "CLAUDE.md"),
            new ImportedFile("docs/more/naming.md", TokenEstimate.Of("PascalCase everywhere."), 2, "docs/style.md"),
        }));
        Assert.That(chain.Tokens, Is.EqualTo(chain.Files.Sum(file => file.Tokens)));
        Assert.That(chain.External, Is.Empty);
        Assert.That(chain.Skipped, Is.Empty);
    }

    [Test]
    public void CyclesAreFollowedOnce()
    {
        WriteFile("a.md", "@b.md");
        WriteFile("b.md", "@a.md and @CLAUDE.md");

        var chain = Follow("@a.md");

        Assert.That(chain.Files.Select(file => (file.Path, file.Depth)), Is.EqualTo(new[] { ("a.md", 1), ("b.md", 2) }));
        Assert.That(chain.Skipped, Is.Empty);
    }

    [Test]
    public void ImportsDeeperThanFiveHopsAreSkipped()
    {
        for (var level = 1; level <= 7; level++)
            WriteFile($"d{level}.md", $"@d{level + 1}.md");

        var chain = Follow("@d1.md");

        Assert.That(chain.Files.Select(file => file.Path), Is.EqualTo(new[] { "d1.md", "d2.md", "d3.md", "d4.md", "d5.md" }));
        Assert.That(chain.Files[^1].Depth, Is.EqualTo(ClaudeMdImports.MaxDepth));
        Assert.That(chain.Skipped, Is.EqualTo(new[] { new SkippedImport("d6.md", "d5.md", SkipReason.TooDeep) }));
    }

    [Test]
    public void MissingFilesAreSkippedAndHomePathsAreExternal()
    {
        var chain = Follow("- @missing.md\n- @~/.claude/my-rules.md\n");

        Assert.That(chain.Files, Is.Empty);
        Assert.That(chain.Skipped, Is.EqualTo(new[] { new SkippedImport("missing.md", "CLAUDE.md", SkipReason.Missing) }));
        Assert.That(chain.External, Is.EqualTo(new[] { new ExternalImport("~/.claude/my-rules.md", "CLAUDE.md") }));
    }

    [Test]
    public void ImportsInCodeFencesCodeSpansAndEmailAddressesAreIgnored()
    {
        WriteFile("fenced.md", "x");
        WriteFile("spanned.md", "x");
        WriteFile("real.md", "x");

        var chain = Follow("```sh\n@fenced.md\n```\nUse `@spanned.md` or mail me@example.com.\nAlso read @real.md.\n");

        Assert.That(chain.Files.Select(file => file.Path), Is.EqualTo(new[] { "real.md" }));
        Assert.That(chain.Skipped, Is.Empty);
    }
}
