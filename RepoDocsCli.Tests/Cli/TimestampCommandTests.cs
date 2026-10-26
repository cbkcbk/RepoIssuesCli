using System.Text.Json;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Cli;

public class TimestampCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    const string Billing = "feature: billing\nstatus: shipped\ncreated: 2026-10-20T09:00:00Z\nentries:\n  - id: rounding\n    kind: rule\n    summary: Round half to even\n    created: 2026-10-20T09:00:00Z\n";

    [Test]
    public void TouchEntryBumpsUpdated()
    {
        repo.Run("init");
        repo.WriteDocsFile("billing.yaml", Billing);

        var result = JsonDocument.Parse(repo.Run("touch", "billing/rounding", "--json").Output).RootElement;

        Assert.That(result.GetProperty("target").GetString(), Is.EqualTo("billing/rounding"));
        Assert.That(result.GetProperty("files")[0].GetString(), Is.EqualTo(".repo-docs/billing.yaml"));
        Assert.That(repo.Store.LoadFeature("billing").Entries.Single().Updated, Is.EqualTo(result.GetProperty("updated").GetDateTimeOffset()));
        Assert.That(repo.Store.LoadFeature("billing").Updated, Is.Null);
    }

    [TestCase("billing/missing", ExitCodes.NotFound)]
    [TestCase("missing", ExitCodes.NotFound)]
    [TestCase("billing/rounding/extra", ExitCodes.Usage)]
    [TestCase("repo", ExitCodes.Usage)]
    public void TouchRejectsBadTargets(string target, int exitCode)
    {
        repo.Run("init");
        repo.WriteDocsFile("billing.yaml", Billing);

        var result = repo.Run("touch", target);

        Assert.That(result.ExitCode, Is.EqualTo(exitCode));
        Assert.That(result.Errors, Does.Contain("Error:"));
    }

    [Test]
    public void TouchWithoutTargetStampsHandEdits()
    {
        repo.InitGit();
        repo.Run("init");
        repo.WriteDocsFile("billing.yaml", Billing);
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "billing");
        repo.WriteDocsFile("billing.yaml", Billing.Replace("Round half to even", "Round once"));

        var result = repo.Run("touch");
        var again = repo.Run("touch");

        Assert.That(result.Output, Is.EqualTo("Stamped .repo-docs/billing.yaml\n"));
        Assert.That(repo.Store.LoadFeature("billing").Entries.Single().Updated, Is.Not.Null);
        Assert.That(again.Output, Is.EqualTo("No hand edits to stamp.\n"));
    }

    [Test]
    public void MergeDriverMergesPerEntry()
    {
        var ancestor = Write("ancestor.yaml", Billing);
        var current = Write("current.yaml", Billing.Replace("Round half to even", "Ours") + "    updated: 2026-10-22T09:00:00Z\n    read: 2026-10-25\n");
        var other = Write("other.yaml", Billing.Replace("Round half to even", "Theirs") + "    updated: 2026-10-23T09:00:00Z\n"
            + "  - id: vat\n    kind: fact\n    summary: VAT is 25%\n");

        var result = repo.RunWithoutRoot("merge-driver", ancestor, current, other, ".repo-docs/billing.yaml");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var merged = DocsYaml.Deserialize<Feature>(File.ReadAllText(current))!;
        Assert.That(merged.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "rounding", "vat" }));
        Assert.That(merged.Entries[0].Summary, Is.EqualTo("Theirs"));
        Assert.That(merged.Entries[0].Read, Is.EqualTo(new DateOnly(2026, 10, 25)));
    }

    [Test]
    public void MergeDriverKeepsCleanTextualMerges()
    {
        var ancestor = Write("ancestor.yaml", Billing);
        var current = Write("current.yaml", Billing.Replace("status: shipped", "status: deprecated"));
        var other = Write("other.yaml", Billing + "    read: 2026-10-25\n");

        var result = repo.RunWithoutRoot("merge-driver", ancestor, current, other, ".repo-docs/billing.yaml");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(File.ReadAllText(current), Is.EqualTo(Billing.Replace("status: shipped", "status: deprecated") + "    read: 2026-10-25\n"));
    }

    [Test]
    public void MergeDriverLeavesUnmergeableConflictsMarked()
    {
        var ancestor = Write("ancestor.yaml", "version: 1\nkinds: {}\n");
        var current = Write("current.yaml", "version: 1\nkinds:\n  rule: {}\n");
        var other = Write("other.yaml", "version: 1\nkinds:\n  fact: {}\n");

        var result = repo.RunWithoutRoot("merge-driver", ancestor, current, other, ".repo-docs/config.yaml");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.ValidationFailed));
        Assert.That(ConflictMarkers.Contains(File.ReadAllText(current)), Is.True);
    }

    string Write(string name, string content)
    {
        var path = Path.Combine(repo.Root, name);
        File.WriteAllText(path, content);
        return path;
    }
}
