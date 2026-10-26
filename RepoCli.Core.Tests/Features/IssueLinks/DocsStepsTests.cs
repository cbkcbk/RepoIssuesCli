using RepoCli.Core.Features.IssueLinks;

namespace RepoCli.Core.Tests.Features.IssueLinks;

public class DocsStepsTests
{
    TempFolder folder = null!;

    [SetUp]
    public void SetUp() => folder = new TempFolder();

    [TearDown]
    public void TearDown() => folder.Dispose();

    DocsSteps Steps => new(folder.Root);

    string DocsPath(string fileName) => Path.Combine(folder.Subfolder(RepoLocator.DocsFolderName), fileName);

    void WriteDocs(string fileName, string yaml) => File.WriteAllText(DocsPath(fileName), yaml);

    const string FlowStyle = """
                             feature: billing
                             status: in-progress
                             created: 2026-10-20T09:15:00Z
                             entries:
                               - id: rollout
                                 kind: plan
                                 summary: Roll out the new invoices
                                 steps:
                                   - title: Schema and store
                                     issues: [23, 24]
                                   - title: Write the migration guide
                                   - title: Cut over
                                     issues: [24]
                                 created: 2026-10-20T09:15:00Z
                               - id: vat-cache
                                 kind: gotcha
                                 summary: VAT rates are cached for an hour

                             """;

    const string HandWritten = """
                               # Billing plan, kept by hand
                               feature: billing
                               entries:
                                 # the rollout
                                 - id: rollout   # main plan
                                   kind: plan
                                   summary: Roll out
                                   steps:
                                     - title: Schema   # first
                                       issues:
                                         - 23   # schema
                                         - '7'
                                         - "230"
                                     - title: Later
                                       issues: [ 7,23 ,  9 ]  # spaced
                               """;

    [Test]
    public void FindsFlowStyleReferences()
    {
        WriteDocs("billing.yaml", FlowStyle);

        var scan = Steps.Find();

        Assert.That(scan.Errors, Is.Empty);
        Assert.That(scan.References, Is.EqualTo(new[]
        {
            new DocsReference("billing/rollout", "Schema and store", 23, ".repo-docs/billing.yaml"),
            new DocsReference("billing/rollout", "Schema and store", 24, ".repo-docs/billing.yaml"),
            new DocsReference("billing/rollout", "Cut over", 24, ".repo-docs/billing.yaml"),
        }));
        Assert.That(Steps.Find(24).References.Select(reference => reference.Describe()),
            Is.EqualTo(new[] { "billing/rollout (Schema and store)", "billing/rollout (Cut over)" }));
    }

    [Test]
    public void FindsBlockStyleReferencesInAHandWrittenFileWithComments()
    {
        WriteDocs("billing.yaml", HandWritten);

        var scan = Steps.Find();

        Assert.That(scan.Errors, Is.Empty);
        Assert.That(scan.References.Select(reference => (reference.StepTitle, reference.IssueNumber)),
            Is.EqualTo(new[] { ("Schema", 23), ("Schema", 7), ("Schema", 230), ("Later", 7), ("Later", 23), ("Later", 9) }));
    }

    [Test]
    public void FindsRepoEntriesAndSkipsConfig()
    {
        WriteDocs("repo.yaml", """
                               entries:
                                 - id: cleanup
                                   kind: plan
                                   summary: Tidy up
                                   steps:
                                     - title: Remove dead code
                                       issues: [5]
                               """);
        WriteDocs("config.yaml", """
                                 version: 1
                                 entries:
                                   - id: odd
                                     steps:
                                       - title: Not a doc
                                         issues: [5]
                                 """);

        var scan = Steps.Find(5);

        Assert.That(scan.References, Is.EqualTo(new[] { new DocsReference("repo/cleanup", "Remove dead code", 5, ".repo-docs/repo.yaml") }));
    }

    [Test]
    public void InvalidFilesAreSkippedAndReported()
    {
        WriteDocs("billing.yaml", FlowStyle);
        WriteDocs("broken.yaml", "entries: [\n  - id: x\n    steps: {");
        WriteDocs("dupes.yaml", "feature: dupes\nfeature: again\n");
        File.WriteAllBytes(DocsPath("binary.yaml"), [0x66, 0xFF, 0xFE, 0x0A]);
        WriteDocs("scalar.yaml", "just text\n");
        WriteDocs("empty.yaml", "");

        var scan = Steps.Find();

        Assert.That(scan.References, Has.Count.EqualTo(3));
        Assert.That(scan.Errors.Select(error => error.File),
            Is.EquivalentTo(new[] { ".repo-docs/binary.yaml", ".repo-docs/broken.yaml", ".repo-docs/dupes.yaml" }));
        Assert.That(Steps.Renumber(24, 30).Errors, Has.Count.EqualTo(3));
    }

    [Test]
    public void MissingDocsFolderIsANoOp()
    {
        using (Steps.Lock())
        {
            Assert.That(Steps.Find().References, Is.Empty);
            Assert.That(Steps.Renumber(1, 2).References, Is.Empty);
        }

        Assert.That(Directory.Exists(Path.Combine(folder.Root, RepoLocator.DocsFolderName)), Is.False);
    }

    [Test]
    public void RenumberRewritesOnlyTheMatchingNumbersInFlowStyle()
    {
        WriteDocs("billing.yaml", FlowStyle);

        DocsReferenceScan result;
        using (Steps.Lock())
            result = Steps.Renumber(24, 41);

        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.References.Select(reference => reference.Describe()), Is.EqualTo(new[] { "billing/rollout (Schema and store)", "billing/rollout (Cut over)" }));
        Assert.That(result.References.Select(reference => reference.IssueNumber), Is.All.EqualTo(41));
        Assert.That(File.ReadAllText(DocsPath("billing.yaml")), Is.EqualTo(FlowStyle.Replace("[23, 24]", "[23, 41]").Replace("[24]", "[41]")));
    }

    [Test]
    public void RenumberKeepsEveryOtherByteOfAHandWrittenFile()
    {
        var crlf = "﻿" + HandWritten.ReplaceLineEndings("\r\n") + "\r\n# ünïcödé trailer\r\n";
        File.WriteAllText(DocsPath("billing.yaml"), crlf, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using (Steps.Lock())
        {
            Assert.That(Steps.Renumber(23, 1234).References, Has.Count.EqualTo(2));
            Assert.That(Steps.Renumber(7, 8).References, Has.Count.EqualTo(2));
        }

        var expected = crlf
            .Replace("- 23   # schema", "- 1234   # schema")
            .Replace("[ 7,23 ,", "[ 8,1234 ,")
            .Replace("- '7'", "- '8'");
        Assert.That(File.ReadAllBytes(DocsPath("billing.yaml")), Is.EqualTo(new System.Text.UTF8Encoding(false).GetBytes(expected)));
        Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(DocsPath("x"))!, "*.tmp"), Is.Empty);
    }

    [Test]
    public void RenumberLeavesFilesWithoutTheNumberUntouched()
    {
        WriteDocs("billing.yaml", FlowStyle);
        WriteDocs("repo.yaml", "entries: []\n");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(DocsPath("repo.yaml"), stamp);

        using (Steps.Lock())
            Steps.Renumber(23, 99);

        Assert.That(File.GetLastWriteTimeUtc(DocsPath("repo.yaml")), Is.EqualTo(stamp));
    }

    [Test]
    public void TaggedNumbersAreReportedInsteadOfGuessed()
    {
        WriteDocs("billing.yaml", "entries:\n  - id: p\n    steps:\n      - title: T\n        issues: [!!int 5, 6]\n");
        var before = File.ReadAllText(DocsPath("billing.yaml"));

        DocsReferenceScan result;
        using (Steps.Lock())
            result = Steps.Renumber(5, 7);

        Assert.That(result.References, Is.Empty);
        Assert.That(result.Errors.Single().Message, Does.Contain("docs edit"));
        Assert.That(File.ReadAllText(DocsPath("billing.yaml")), Is.EqualTo(before));
    }
}