using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class DocsStoreTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    static Feature NewFeature(string id) => new()
    {
        Id = id,
        Status = FeatureStatus.InProgress,
        Summary = "Bills customers",
        When = new FeatureWhen { Paths = ["src/Features/Billing/"] },
        Entries =
        [
            new FeatureEntry { Id = "rounding", Kind = "rule", Summary = "Round half to even", Body = "Use MidpointRounding.ToEven.\nNever round twice.\n" },
        ],
    };

    [Test]
    public void FeatureRoundTripsThroughYaml()
    {
        var feature = NewFeature("billing");
        repo.Store.Save(feature);

        var yaml = repo.ReadDocsFile("billing.yaml");
        var loaded = repo.Store.LoadFeature("billing");

        Assert.That(yaml, Does.StartWith("feature: billing\nstatus: in-progress\nsummary: Bills customers\n"));
        Assert.That(yaml, Does.Contain("body: |"));
        Assert.That(DocsYaml.Serialize(loaded), Is.EqualTo(yaml));
        Assert.That(loaded.Entries.Single().Body, Is.EqualTo(feature.Entries.Single().Body));
    }

    [Test]
    public void RepoAndConfigRoundTrip()
    {
        repo.Store.SaveRepo(new RepoDocs { Summary = "Monorepo", Entries = [new FeatureEntry { Id = "style", Kind = "rule", Summary = "Use tabs" }] });
        repo.Store.SaveConfig(DocsConfig.Default());

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Errors, Is.Empty);
        Assert.That(snapshot.Repo.Summary, Is.EqualTo("Monorepo"));
        Assert.That(snapshot.Repo.Entries.Single().Summary, Is.EqualTo("Use tabs"));
        Assert.That(snapshot.Config.Kinds.Keys, Is.EqualTo(new[] { "rule", "fact", "gotcha", "howto", "decision", "plan" }));
    }

    [Test]
    public void LoadReturnsFeaturesSortedAndSkipsRepoAndConfig()
    {
        repo.Store.Initialize();
        repo.Store.Save(NewFeature("search"));
        repo.Store.Save(NewFeature("billing"));

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Features.Select(feature => feature.Id), Is.EqualTo(new[] { "billing", "search" }));
        Assert.That(snapshot.Find("search")?.Status, Is.EqualTo(FeatureStatus.InProgress));
        Assert.That(snapshot.Errors, Is.Empty);
    }

    [Test]
    public void HandWrittenMinimalFeatureLoadsWithDefaults()
    {
        repo.WriteDocsFile("billing.yaml", "feature: billing\nwhen:\nentries:\n");

        var feature = repo.Store.LoadFeature("billing");

        Assert.That(feature.Status, Is.EqualTo(FeatureStatus.Proposed));
        Assert.That(feature.Summary, Is.Null);
        Assert.That(feature.When.Paths, Is.Empty);
        Assert.That(feature.Entries, Is.Empty);
    }

    [Test]
    public void InvalidFilesAreReportedNotThrown()
    {
        repo.Store.Initialize();
        repo.WriteDocsFile("broken.yaml", "feature: [unclosed");
        repo.WriteDocsFile("empty.yaml", "");
        repo.WriteDocsFile("unknown.yaml", "feature: unknown\ncolour: red\n");
        repo.WriteDocsFile("mismatch.yaml", "feature: other\n");
        repo.WriteDocsFile("status.yaml", "feature: status\nstatus: finished\n");
        repo.WriteDocsFile("config.yaml", "kinds: [");
        repo.Store.Save(NewFeature("billing"));

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Features.Select(feature => feature.Id), Is.EqualTo(new[] { "billing" }));
        Assert.That(snapshot.Errors.Select(error => error.FileName),
            Is.EquivalentTo(new[] { "broken.yaml", "config.yaml", "empty.yaml", "mismatch.yaml", "status.yaml", "unknown.yaml" }));
        Assert.That(snapshot.Config.Kinds, Is.Not.Empty);
    }

    [Test]
    public void LoadFeatureThrowsForInvalidOrMissingFiles()
    {
        repo.WriteDocsFile("broken.yaml", "feature: [unclosed");

        Assert.Throws<DocsFileException>(() => repo.Store.LoadFeature("broken"));
        Assert.Throws<FeatureNotFoundException>(() => repo.Store.LoadFeature("missing"));
        Assert.Throws<UsageException>(() => repo.Store.LoadFeature("Not Valid"));
    }

    [Test]
    public void SaveInAGitRepositoryStagesNewFiles()
    {
        repo.InitGit();

        repo.Store.Save(NewFeature("billing"));

        Assert.That(repo.Git("diff", "--cached", "--name-only"), Is.EqualTo(".repo-docs/billing.yaml\n"));
    }

    [Test]
    public void RenameUsesGitMove()
    {
        repo.InitGit();
        repo.Store.Save(NewFeature("billing"));
        repo.Git("commit", "-q", "-m", "add billing");

        var renamed = repo.Store.Rename("billing", "invoicing");

        Assert.That(renamed.Id, Is.EqualTo("invoicing"));
        Assert.That(File.Exists(repo.Store.PathFor("billing")), Is.False);
        Assert.That(repo.Store.LoadFeature("invoicing").Summary, Is.EqualTo("Bills customers"));
        Assert.That(repo.Git("status", "--porcelain"), Does.StartWith("RM .repo-docs/billing.yaml -> .repo-docs/invoicing.yaml"));
    }

    [Test]
    public void RenameOntoAnExistingFeatureFails()
    {
        repo.Store.Save(NewFeature("billing"));
        repo.Store.Save(NewFeature("search"));

        Assert.Throws<UsageException>(() => repo.Store.Rename("billing", "search"));
        Assert.That(repo.Store.LoadFeature("billing").Id, Is.EqualTo("billing"));
    }

    [Test]
    public void DeleteRemovesTheFileWithGit()
    {
        repo.InitGit();
        repo.Store.Save(NewFeature("billing"));
        repo.Git("commit", "-q", "-m", "add billing");

        repo.Store.Delete("billing");

        Assert.That(repo.Store.Exists("billing"), Is.False);
        Assert.That(repo.Git("status", "--porcelain"), Is.EqualTo("D  .repo-docs/billing.yaml\n"));
        Assert.Throws<FeatureNotFoundException>(() => repo.Store.Delete("billing"));
    }

    [Test]
    public void SaveRejectsInvalidIds()
    {
        Assert.Throws<UsageException>(() => repo.Store.Save(NewFeature("repo")));
        Assert.Throws<UsageException>(() => repo.Store.Save(NewFeature("Billing")));
    }

    [Test]
    public void LockIsExclusiveUntilReleased()
    {
        var waiting = new DocsStore(repo.Root) { LockTimeout = TimeSpan.FromMilliseconds(100) };

        using (repo.Store.Lock())
            Assert.Throws<LockTimeoutException>(() => waiting.Lock());

        Assert.DoesNotThrow(() => waiting.Lock().Dispose());
    }

    [Test]
    public void LockFileIsGitIgnoredAndNotAFeature()
    {
        repo.InitGit();
        repo.Store.Initialize();

        using (repo.Store.Lock())
        {
            var snapshot = repo.Store.Load();
            Assert.That(snapshot.Features, Is.Empty);
            Assert.That(snapshot.Errors, Is.Empty);
            Assert.That(repo.Git("status", "--porcelain", "--untracked-files=all"), Does.Not.Contain(".lock"));
        }
    }

    [Test]
    public void LoadOfAnUninitializedRepositoryIsEmpty()
    {
        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Features, Is.Empty);
        Assert.That(snapshot.Errors, Is.Empty);
        Assert.That(repo.Store.IsInitialized, Is.False);
    }
}
