using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class DocsMergeTests
{
    static readonly DateTimeOffset Day1 = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Day2 = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    static FeatureEntry Entry(string id, string summary, DateTimeOffset? updated = null, DateOnly? read = null) =>
        new() { Id = id, Kind = "fact", Summary = summary, Created = Day1, Updated = updated, Read = read };

    [Test]
    public void SplitRebuildsBothSidesAndTheAncestor()
    {
        const string text = "a\n<<<<<<< ours\nb\n||||||| base\nc\n=======\nd\n>>>>>>> theirs\ne\n";

        var sides = ConflictMarkers.Split(text);

        Assert.That(sides, Is.EqualTo(new ConflictSides("a\nb\ne\n", "a\nd\ne\n", "a\nc\ne\n")));
    }

    [Test]
    public void SplitWithoutAncestorSectionsHasNoAncestor()
    {
        var sides = ConflictMarkers.Split("<<<<<<< HEAD\r\nb\r\n=======\r\nd\r\n>>>>>>> other\r\n");

        Assert.That(sides?.Ancestor, Is.Null);
        Assert.That(sides?.Ours, Is.EqualTo("b\r\n"));
    }

    [TestCase("plain\n")]
    [TestCase("<<<<<<< ours\nb\n")]
    [TestCase("=======\n")]
    public void SplitRejectsTextWithoutCompleteConflicts(string text) =>
        Assert.That(ConflictMarkers.Split(text), Is.Null);

    [Test]
    public void LaterUpdatedWinsAndReadIsMergedIndependently()
    {
        var merged = DocsMerge.Merge(null, Entry("a", "ours", Day2, new DateOnly(2026, 10, 20)), Entry("a", "theirs", Day1, new DateOnly(2026, 10, 25)));

        Assert.That(merged.Summary, Is.EqualTo("ours"));
        Assert.That(merged.Updated, Is.EqualTo(Day2));
        Assert.That(merged.Read, Is.EqualTo(new DateOnly(2026, 10, 25)));
    }

    [Test]
    public void SideThatChangedNothingLosesToTheEditedSide()
    {
        var ancestor = Entry("a", "base", Day2);

        var merged = DocsMerge.Merge(ancestor, ancestor, Entry("a", "theirs", Day1));

        Assert.That(merged.Summary, Is.EqualTo("theirs"));
    }

    [Test]
    public void EntriesOnOneSideAreKeptUnlessTheOtherSideDeletedThemUnchanged()
    {
        IReadOnlyList<FeatureEntry> ancestor = [Entry("kept", "k"), Entry("deleted", "d"), Entry("edited", "e")];
        IReadOnlyList<FeatureEntry> ours = [Entry("kept", "k"), Entry("deleted", "d"), Entry("ours-only", "o")];
        IReadOnlyList<FeatureEntry> theirs = [Entry("kept", "k"), Entry("edited", "e2", Day2), Entry("theirs-only", "t")];

        var merged = DocsMerge.MergeEntries(ancestor, ours, theirs);

        Assert.That(merged.Select(entry => entry.Id), Is.EqualTo(new[] { "kept", "ours-only", "edited", "theirs-only" }));
    }

    [Test]
    public void WithoutAnAncestorEveryOneSidedEntryIsKept()
    {
        var merged = DocsMerge.MergeEntries(null, [Entry("a", "a")], [Entry("b", "b")]);

        Assert.That(merged.Select(entry => entry.Id), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void FeatureFieldsFollowTheLaterFeatureUpdate()
    {
        var ours = new Feature { Id = "billing", Summary = "Ours", Created = Day2, Updated = Day1 };
        var theirs = new Feature { Id = "billing", Summary = "Theirs", Status = FeatureStatus.Shipped, Created = Day1, Updated = Day2 };

        var merged = DocsMerge.Merge(null, ours, theirs);

        Assert.That(merged.Summary, Is.EqualTo("Theirs"));
        Assert.That(merged.Status, Is.EqualTo(FeatureStatus.Shipped));
        Assert.That(merged.Created, Is.EqualTo(Day1));
        Assert.That(merged.Updated, Is.EqualTo(Day2));
    }

    [Test]
    public void ResolveMergesRepoFiles()
    {
        const string text = """
            entries:
            <<<<<<< ours
              - id: style
                kind: rule
                summary: Use tabs
            =======
              - id: naming
                kind: rule
                summary: PascalCase types
            >>>>>>> theirs

            """;

        var resolved = DocsMerge.Resolve("repo.yaml", text.ReplaceLineEndings("\n"));

        Assert.That(resolved, Is.Not.Null);
        Assert.That(DocsYaml.Deserialize<RepoDocs>(resolved!)!.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "style", "naming" }));
    }

    [Test]
    public void ResolveGivesUpOnInvalidYaml() =>
        Assert.That(DocsMerge.Resolve("billing.yaml", "feature: billing\n<<<<<<< ours\ncolour: red\n=======\nstatus: shipped\n>>>>>>> theirs\n"), Is.Null);
}
