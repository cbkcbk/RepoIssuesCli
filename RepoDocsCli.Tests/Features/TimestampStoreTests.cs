using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class TimestampStoreTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    static readonly Anchor MoneyAnchor = new("src/Money.cs", 7, 1, "112233445566", "665544332211");

    TempRepo repo = null!;
    TestClock clock = null!;
    DocsStore store = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        clock = new TestClock(Start);
        store = new DocsStore(repo.Root) { Clock = clock };
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    static Feature Billing() => new()
    {
        Id = "billing",
        Summary = "Invoices",
        Entries = [new FeatureEntry { Id = "rounding", Kind = "rule", Summary = "Round half to even", When = new WhenHints { Anchors = [MoneyAnchor] } }],
    };

    FeatureEntry Rounding() => store.LoadFeature("billing").Entries.Single(entry => entry.Id == "rounding");

    [Test]
    public void SaveStampsCreatedOnNewFeaturesAndEntries()
    {
        store.Save(Billing());

        var feature = store.LoadFeature("billing");
        Assert.That(feature.Created, Is.EqualTo(Start));
        Assert.That(feature.Updated, Is.Null);
        Assert.That(feature.Entries.Single().Created, Is.EqualTo(Start));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Not.Contain("updated:"));
    }

    [Test]
    public void SaveBumpsUpdatedOnBodyEdits()
    {
        store.Save(Billing());
        clock.Advance(TimeSpan.FromDays(1));

        var feature = store.LoadFeature("billing");
        store.Save(feature with { Entries = [feature.Entries[0] with { Body = "Never round twice.\n" }] });

        Assert.That(Rounding().Updated, Is.EqualTo(Start.AddDays(1)));
        Assert.That(Rounding().Created, Is.EqualTo(Start));
        Assert.That(store.LoadFeature("billing").Updated, Is.Null);
    }

    [Test]
    public void SaveIgnoresAnchorLineMoves()
    {
        store.Save(Billing());
        clock.Advance(TimeSpan.FromDays(1));

        var feature = store.LoadFeature("billing");
        store.Save(feature with { Entries = [feature.Entries[0] with { When = new WhenHints { Anchors = [MoneyAnchor with { Line = 12 }] } }] });

        Assert.That(Rounding().When!.Anchors[0].Line, Is.EqualTo(12));
        Assert.That(Rounding().Updated, Is.Null);
    }

    [Test]
    public void ReadOnTheSameDayWritesNothing()
    {
        store.Save(Billing());
        Assert.That(store.MarkRead("billing", "rounding"), Is.True);
        var before = repo.ReadDocsFile("billing.yaml");
        File.SetLastWriteTimeUtc(store.PathFor("billing"), new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        clock.Advance(TimeSpan.FromHours(3));

        Assert.That(store.MarkRead("billing", "rounding"), Is.False);
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(store.PathFor("billing")).Year, Is.EqualTo(2000));
    }

    [Test]
    public void ReadOnANewDayWritesTheDateAndLeavesUpdatedAlone()
    {
        store.Save(Billing());
        clock.Advance(TimeSpan.FromDays(2));

        Assert.That(store.MarkRead("billing", "rounding"), Is.True);
        Assert.That(Rounding().Read, Is.EqualTo(new DateOnly(2026, 10, 22)));
        Assert.That(Rounding().Updated, Is.Null);
        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Contain("    read: 2026-10-22\n"));
    }

    [Test]
    public void RepoEntriesCanBeReadAndTouched()
    {
        store.Initialize();
        store.SaveRepo(new RepoDocs { Entries = [new FeatureEntry { Id = "style", Kind = "rule", Summary = "Use tabs" }] });
        clock.Advance(TimeSpan.FromDays(1));

        store.MarkRead("repo", "style");
        store.Touch("repo", "style");

        var entry = store.Load().Repo.Entries.Single();
        Assert.That(entry.Created, Is.EqualTo(Start));
        Assert.That(entry.Updated, Is.EqualTo(Start.AddDays(1)));
        Assert.That(entry.Read, Is.EqualTo(new DateOnly(2026, 10, 21)));
    }

    [Test]
    public void UnknownEntriesAreNotFound()
    {
        store.Save(Billing());

        Assert.Throws<EntryNotFoundException>(() => store.MarkRead("billing", "missing"));
        Assert.Throws<FeatureNotFoundException>(() => store.MarkRead("missing", "rounding"));
    }

    [Test]
    public void TouchBumpsUpdatedWithoutAnEdit()
    {
        store.Save(Billing());
        clock.Advance(TimeSpan.FromDays(1));

        store.Touch("billing", "rounding");
        store.Touch("billing");

        Assert.That(Rounding().Updated, Is.EqualTo(Start.AddDays(1)));
        Assert.That(store.LoadFeature("billing").Updated, Is.EqualTo(Start.AddDays(1)));
    }

    [Test]
    public void HandEditsSinceHeadAreStampedOnce()
    {
        repo.InitGit();
        store.Save(Billing());
        repo.Git("commit", "-q", "-m", "billing");
        clock.Advance(TimeSpan.FromDays(1));

        var yaml = repo.ReadDocsFile("billing.yaml");
        repo.WriteDocsFile("billing.yaml", yaml.Replace("summary: Round half to even", "summary: Round half to even, once")
            + "  - id: vat\n    kind: fact\n    summary: VAT is 25%\n");

        Assert.That(store.StampHandEdits(), Is.EqualTo(new[] { ".repo-docs/billing.yaml" }));
        var feature = store.LoadFeature("billing");
        Assert.That(feature.Entries[0].Updated, Is.EqualTo(Start.AddDays(1)));
        Assert.That(feature.Entries[1].Created, Is.EqualTo(Start.AddDays(1)));
        Assert.That(feature.Updated, Is.Null);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.That(store.StampHandEdits(), Is.Empty);
        Assert.That(Rounding().Updated, Is.EqualTo(Start.AddDays(1)));
    }

    [Test]
    public void HandEditedLineHintIsNotStamped()
    {
        repo.InitGit();
        store.Save(Billing());
        repo.Git("commit", "-q", "-m", "billing");
        clock.Advance(TimeSpan.FromDays(1));

        repo.WriteDocsFile("billing.yaml", repo.ReadDocsFile("billing.yaml").Replace("line: 7", "line: 30"));

        Assert.That(store.StampHandEdits(), Is.Empty);
    }

    [Test]
    public void UncommittedFeatureFilesGetCreated()
    {
        repo.InitGit();
        store.Initialize();
        repo.WriteDocsFile("search.yaml", "feature: search\nentries:\n  - id: ranking\n    kind: fact\n    summary: BM25\n");

        Assert.That(store.StampHandEdits(), Is.EqualTo(new[] { ".repo-docs/search.yaml" }));
        Assert.That(store.LoadFeature("search").Created, Is.EqualTo(Start));
        Assert.That(store.LoadFeature("search").Entries.Single().Created, Is.EqualTo(Start));
    }

    [Test]
    public void GitMergeConflictsAreResolvedPerEntry()
    {
        repo.InitGit();
        store.Initialize();
        store.Save(Billing() with
        {
            Entries = [.. Billing().Entries, new FeatureEntry { Id = "vat", Kind = "fact", Summary = "VAT is 25%" }],
        });
        repo.Git("commit", "-q", "-m", "base");
        var main = repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        repo.Git("checkout", "-q", "-b", "other");
        clock.Advance(TimeSpan.FromDays(2));
        Edit(entry => entry.Id == "rounding" ? entry with { Summary = "Theirs: round once" } : entry, new FeatureEntry { Id = "theirs-only", Kind = "fact", Summary = "Added by them" });
        store.MarkRead("billing", "vat");
        repo.Git("commit", "-q", "-am", "theirs");

        repo.Git("checkout", "-q", main);
        clock.Now = Start.AddDays(1);
        Edit(entry => entry.Id == "rounding" ? entry with { Summary = "Ours: round half to even" } : entry, new FeatureEntry { Id = "ours-only", Kind = "fact", Summary = "Added by us" });
        clock.Now = Start.AddDays(3);
        store.MarkRead("billing", "rounding");
        repo.Git("commit", "-q", "-am", "ours");

        Assert.That(Git.Run(repo.Root, "merge", "-q", "other")?.ExitCode, Is.Not.Zero);
        Assert.That(ConflictMarkers.Contains(repo.ReadDocsFile("billing.yaml")), Is.True);

        var results = store.ResolveConflicts();

        Assert.That(results, Is.EqualTo(new[] { new ConflictResolution(".repo-docs/billing.yaml", true) }));
        var merged = store.LoadFeature("billing");
        Assert.That(merged.Entries.Select(entry => entry.Id), Is.EquivalentTo(new[] { "rounding", "vat", "ours-only", "theirs-only" }));
        var rounding = merged.Entries.Single(entry => entry.Id == "rounding");
        Assert.That(rounding.Summary, Is.EqualTo("Theirs: round once"));
        Assert.That(rounding.Updated, Is.EqualTo(Start.AddDays(2)));
        Assert.That(rounding.Read, Is.EqualTo(new DateOnly(2026, 10, 23)));
        Assert.That(merged.Entries.Single(entry => entry.Id == "vat").Read, Is.EqualTo(new DateOnly(2026, 10, 22)));
        Assert.That(repo.Git("diff", "--name-only", "--diff-filter=U"), Is.Empty);
    }

    [Test]
    public void ConfigConflictsAreReportedUnresolved()
    {
        store.Initialize();
        repo.WriteDocsFile("config.yaml", "version: 1\n<<<<<<< ours\nkinds: {}\n=======\nkinds:\n  rule: {}\n>>>>>>> theirs\n");

        Assert.That(store.ResolveConflicts(), Is.EqualTo(new[] { new ConflictResolution(".repo-docs/config.yaml", false) }));
        Assert.That(ConflictMarkers.Contains(repo.ReadDocsFile("config.yaml")), Is.True);
    }

    void Edit(Func<FeatureEntry, FeatureEntry> change, FeatureEntry added)
    {
        var feature = store.LoadFeature("billing");
        store.Save(feature with { Entries = [.. feature.Entries.Select(change), added] });
    }
}
