using RepoCli.Core.Features.Anchors;
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
        When = new WhenHints { Paths = ["src/Features/Billing/"] },
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
        Assert.That(DocsYaml.Serialize(snapshot.Config), Is.EqualTo(repo.ReadDocsFile("config.yaml")));
    }

    [Test]
    public void FullSchemaRoundTripsThroughYaml()
    {
        repo.WriteDocsFile("billing.yaml", DocsYaml.Serialize(FullFeature()));

        var yaml = repo.ReadDocsFile("billing.yaml");
        var loaded = repo.Store.LoadFeature("billing");

        Assert.That(yaml, Is.EqualTo(FullFeatureYaml.ReplaceLineEndings("\n")));
        Assert.That(DocsYaml.Serialize(loaded), Is.EqualTo(yaml));
        Assert.That(loaded.When.Anchors[0], Is.EqualTo(FullFeature().When.Anchors[0]));
        Assert.That(loaded.Entries[0].When!.Anchors[0], Is.EqualTo(FullFeature().Entries[0].When!.Anchors[0]));
        Assert.That(loaded.Entries[0].Read, Is.EqualTo(new DateOnly(2026, 10, 26)));
        Assert.That(loaded.Entries[1].When, Is.Null);
    }

    [Test]
    public void TimestampsAreStoredAsUtcSeconds()
    {
        repo.Store.Save(NewFeature("billing") with { Created = new DateTimeOffset(2026, 10, 26, 16, 33, 11, 500, TimeSpan.FromHours(2)) });

        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Contain("\ncreated: 2026-10-26T14:33:11Z\n"));
        Assert.That(repo.Store.LoadFeature("billing").Created, Is.EqualTo(new DateTimeOffset(2026, 10, 26, 14, 33, 11, TimeSpan.Zero)));
    }

    [TestCase("feature: billing\ncreated: yesterday\n")]
    [TestCase("feature: billing\nentries:\n  - id: a\n    kind: rule\n    summary: s\n    read: 26/10/2026\n")]
    [TestCase("feature: billing\nwhen:\n  anchors:\n    - path: a.cs\n      line: 1\n")]
    [TestCase("feature: billing\nwhen:\n  anchors:\n    - path: a.cs\n      line: 1\n      span: 1\n      context: abc\n      body: def\n      colour: red\n")]
    [TestCase("feature: billing\nentries:\n  - id: a\n    kind: rule\n    summary: s\n    claims: []\n")]
    public void InvalidSchemaValuesAreFileErrors(string content)
    {
        repo.WriteDocsFile("billing.yaml", content);

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Features, Is.Empty);
        Assert.That(snapshot.Errors.Single().FileName, Is.EqualTo("billing.yaml"));
    }

    [Test]
    public void EntryWithoutWhenHintsOmitsWhen()
    {
        repo.WriteDocsFile("billing.yaml", "feature: billing\nentries:\n  - id: a\n    kind: rule\n    summary: s\n    when: {}\n");

        var feature = repo.Store.LoadFeature("billing");

        Assert.That(feature.Entries.Single().When, Is.Null);
        Assert.That(DocsYaml.Serialize(feature), Is.EqualTo("feature: billing\nstatus: proposed\nwhen: {}\nentries:\n  - id: a\n    kind: rule\n    summary: s\n"));
    }

    [Test]
    public void InitStampsCreatedOnPrefilledFeatures()
    {
        var now = new DateTimeOffset(2026, 10, 26, 14, 33, 11, 250, TimeSpan.Zero);
        repo.Subfolder("App", "Features", "Billing");
        var store = new DocsStore(repo.Root) { Clock = new FixedClock(now) };

        store.Initialize();

        Assert.That(store.LoadFeature("billing").Created, Is.EqualTo(DocsTime.Truncate(now)));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Contain("\ncreated: 2026-10-26T14:33:11Z\n"));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Not.Contain("updated:"));
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    static Feature FullFeature() => new()
    {
        Id = "billing",
        Status = FeatureStatus.InProgress,
        Summary = "Invoices and payments",
        When = new WhenHints
        {
            Paths = ["src/Features/Billing/"],
            Anchors = [new Anchor("src/Features/Billing/Invoice.cs", 42, 3, "a1b2c3d4e5f6", "0f1e2d3c4b5a", "Invoice.Total")],
            Keywords = ["invoice", "vat"],
        },
        Created = new DateTimeOffset(2026, 10, 20, 9, 15, 0, TimeSpan.Zero),
        Updated = new DateTimeOffset(2026, 10, 26, 14, 33, 11, TimeSpan.Zero),
        Entries =
        [
            new FeatureEntry
            {
                Id = "rounding",
                Kind = "rule",
                Summary = "Round half to even",
                When = new WhenHints { Anchors = [new Anchor("src/Features/Billing/Money.cs", 7, 1, "112233445566", "665544332211")] },
                Created = new DateTimeOffset(2026, 10, 20, 9, 15, 0, TimeSpan.Zero),
                Updated = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero),
                Read = new DateOnly(2026, 10, 26),
                Body = "Use MidpointRounding.ToEven.\nNever round twice.\n",
            },
            new FeatureEntry { Id = "vat-cache", Kind = "gotcha", Summary = "VAT rates are cached for an hour" },
        ],
    };

    const string FullFeatureYaml = """
        feature: billing
        status: in-progress
        summary: Invoices and payments
        when:
          paths:
            - src/Features/Billing/
          anchors:
            - path: src/Features/Billing/Invoice.cs
              line: 42
              span: 3
              symbol: Invoice.Total
              context: a1b2c3d4e5f6
              body: 0f1e2d3c4b5a
          keywords:
            - invoice
            - vat
        created: 2026-10-20T09:15:00Z
        updated: 2026-10-26T14:33:11Z
        entries:
          - id: rounding
            kind: rule
            summary: Round half to even
            when:
              anchors:
                - path: src/Features/Billing/Money.cs
                  line: 7
                  span: 1
                  context: 112233445566
                  body: 665544332211
            created: 2026-10-20T09:15:00Z
            updated: 2026-10-21T08:00:00Z
            read: 2026-10-26
            body: |
              Use MidpointRounding.ToEven.
              Never round twice.
          - id: vat-cache
            kind: gotcha
            summary: VAT rates are cached for an hour

        """;

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
        Assert.That(feature.Created, Is.Null);
    }

    [Test]
    public void HandWrittenMinimalEntryLoadsWithDefaults()
    {
        repo.WriteDocsFile("billing.yaml", "feature: billing\nentries:\n  - id: rounding\n    kind: rule\n    summary: Round half to even\n");

        var entry = repo.Store.LoadFeature("billing").Entries.Single();

        Assert.That(entry.When, Is.Null);
        Assert.That(entry.Body, Is.Null);
        Assert.That(entry.Created, Is.Null);
        Assert.That(entry.Updated, Is.Null);
        Assert.That(entry.Read, Is.Null);
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
