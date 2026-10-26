using System.Text.Json;

namespace RepoDocsCli.Tests.Cli;

public class RmCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.InitGit();
        repo.Run("init");
        repo.Run("add", "billing");
        repo.Run("add", "billing/rounding", "--kind", "rule", "--summary", "Round half to even");
        repo.Run("add", "billing/vat", "--kind", "fact", "--summary", "VAT is 25%");
        repo.Run("add", "repo/style", "--kind", "rule", "--summary", "Use file-scoped namespaces");
        repo.Git("commit", "-q", "-m", "docs");
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    [Test]
    public void RmEntryWithForceRemovesOnlyThatEntry()
    {
        var result = repo.Run("rm", "billing/rounding", "--force");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Is.EqualTo("Removed billing/rounding (.repo-docs/billing.yaml)\n"));
        Assert.That(repo.Store.LoadFeature("billing").Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "vat" }));
    }

    [Test]
    public void RmFeatureWithForceRemovesTheFileWithGitRm()
    {
        var json = JsonDocument.Parse(repo.Run("rm", "billing", "--force", "--json").Output).RootElement;

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("billing"));
        Assert.That(json.GetProperty("feature").GetProperty("entries").GetArrayLength(), Is.EqualTo(2));
        Assert.That(repo.Store.Exists("billing"), Is.False);
        Assert.That(repo.Git("diff", "--cached", "--name-status"), Does.Contain("D\t.repo-docs/billing.yaml"));
    }

    [Test]
    public void RmRepoEntryJsonHasTheEntry()
    {
        var json = JsonDocument.Parse(repo.Run("rm", "repo/style", "--force", "--json").Output).RootElement;

        Assert.That(json.GetProperty("address").GetString(), Is.EqualTo("repo/style"));
        Assert.That(json.GetProperty("file").GetString(), Is.EqualTo(".repo-docs/repo.yaml"));
        Assert.That(json.GetProperty("entry").GetProperty("summary").GetString(), Is.EqualTo("Use file-scoped namespaces"));
        Assert.That(repo.Store.Load().Repo.Entries, Is.Empty);
    }

    [TestCase("billing")]
    [TestCase("billing/rounding")]
    public void RmWithoutForceIsAUsageErrorWhenNotInteractive(string target)
    {
        var before = repo.ReadDocsFile("billing.yaml");

        var result = repo.Run("rm", target);

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--force"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
    }

    [TestCase("missing")]
    [TestCase("billing/missing")]
    [TestCase("repo/missing")]
    public void RmMissingTargetsIsNotFound(string target) =>
        Assert.That(repo.Run("rm", target, "--force").ExitCode, Is.EqualTo(ExitCodes.NotFound));

    [TestCase("repo")]
    [TestCase("billing/rounding/extra")]
    public void RmInvalidTargetsIsAUsageError(string target) =>
        Assert.That(repo.Run("rm", target, "--force").ExitCode, Is.EqualTo(ExitCodes.Usage));
}
