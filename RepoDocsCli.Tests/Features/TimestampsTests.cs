using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class TimestampsTests
{
    static readonly DateTimeOffset Created = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Edited = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = new(2026, 10, 26, 14, 33, 11, TimeSpan.Zero);
    static readonly Anchor MoneyAnchor = new("src/Money.cs", 7, 2, "112233445566", "665544332211", "Money.Round");

    static FeatureEntry Entry() => new()
    {
        Id = "rounding",
        Kind = "rule",
        Summary = "Round half to even",
        Body = "Use MidpointRounding.ToEven.\n",
        When = new WhenHints { Anchors = [MoneyAnchor] },
        Created = Created,
        Updated = Edited,
        Read = new DateOnly(2026, 10, 22),
    };

    static Feature Feature(params FeatureEntry[] entries) => new()
    {
        Id = "billing",
        Summary = "Invoices",
        Created = Created,
        Updated = Edited,
        Entries = [.. entries],
    };

    [Test]
    public void BodyEditBumpsUpdated()
    {
        var stamped = Timestamps.Stamp(Entry(), Entry() with { Body = "Round once.\n" }, Now);

        Assert.That(stamped.Updated, Is.EqualTo(Now));
        Assert.That(stamped.Created, Is.EqualTo(Created));
    }

    [Test]
    public void SummaryWhenAndKindEditsBumpUpdated()
    {
        Assert.That(Timestamps.Stamp(Entry(), Entry() with { Summary = "Round to even" }, Now).Updated, Is.EqualTo(Now));
        Assert.That(Timestamps.Stamp(Entry(), Entry() with { When = new WhenHints { Paths = ["src/"] } }, Now).Updated, Is.EqualTo(Now));
        Assert.That(Timestamps.Stamp(Entry(), Entry() with { Kind = "fact" }, Now).Updated, Is.EqualTo(Now));
    }

    [Test]
    public void UnchangedEntryIsReturnedAsIs()
    {
        var current = Entry() with { When = new WhenHints { Anchors = [MoneyAnchor] } };

        Assert.That(Timestamps.Stamp(Entry(), current, Now), Is.SameAs(current));
    }

    [Test]
    public void AnchorLineMoveDoesNotBumpUpdated()
    {
        var current = Entry() with { When = new WhenHints { Anchors = [MoneyAnchor with { Line = 40 }] } };

        Assert.That(Timestamps.Stamp(Entry(), current, Now), Is.SameAs(current));
    }

    [Test]
    public void AnchorHashChangeBumpsUpdated()
    {
        var current = Entry() with { When = new WhenHints { Anchors = [MoneyAnchor with { Body = "aaaaaaaaaaaa" }] } };

        Assert.That(Timestamps.Stamp(Entry(), current, Now).Updated, Is.EqualTo(Now));
    }

    [Test]
    public void ReadChangeAloneDoesNotBumpUpdated()
    {
        var current = Entry() with { Read = new DateOnly(2026, 10, 26) };

        Assert.That(Timestamps.Stamp(Entry(), current, Now), Is.SameAs(current));
    }

    [Test]
    public void TrailingWhitespaceIsNotAnEdit()
    {
        var current = Entry() with { Body = "Use MidpointRounding.ToEven." };

        Assert.That(Timestamps.Stamp(Entry(), current, Now).Updated, Is.EqualTo(Edited));
    }

    [Test]
    public void EditThatAlreadyBumpedUpdatedKeepsIt()
    {
        var bumped = Now.AddHours(-1);
        var current = Entry() with { Body = "Round once.\n", Updated = bumped };

        Assert.That(Timestamps.Stamp(Entry(), current, Now).Updated, Is.EqualTo(bumped));
    }

    [Test]
    public void NewEntryGetsCreatedButNoUpdated()
    {
        var stamped = Timestamps.Stamp(null, Entry() with { Created = null, Updated = null }, Now);

        Assert.That(stamped.Created, Is.EqualTo(Now));
        Assert.That(stamped.Updated, Is.Null);
    }

    [Test]
    public void DroppedTimestampsAreRestoredFromThePreviousVersion()
    {
        var stamped = Timestamps.Stamp(Entry(), Entry() with { Created = null, Updated = null }, Now);

        Assert.That(stamped.Created, Is.EqualTo(Created));
        Assert.That(stamped.Updated, Is.EqualTo(Edited));
    }

    [Test]
    public void NewFeatureAndItsEntriesGetCreated()
    {
        var feature = Feature(Entry() with { Created = null, Updated = null }) with { Created = null, Updated = null };

        var stamped = Timestamps.Stamp(null, feature, Now);

        Assert.That(stamped.Created, Is.EqualTo(Now));
        Assert.That(stamped.Updated, Is.Null);
        Assert.That(stamped.Entries.Single().Created, Is.EqualTo(Now));
    }

    [Test]
    public void EntryAddedToAnExistingFeatureGetsCreatedWithoutBumpingTheFeature()
    {
        var added = Entry() with { Id = "vat", Created = null, Updated = null };

        var stamped = Timestamps.Stamp(Feature(Entry()), Feature(Entry(), added), Now);

        Assert.That(stamped.Updated, Is.EqualTo(Edited));
        Assert.That(stamped.Entries[0].Updated, Is.EqualTo(Edited));
        Assert.That(stamped.Entries[1].Created, Is.EqualTo(Now));
    }

    [Test]
    public void FeatureFieldEditsBumpOnlyTheFeature()
    {
        var stamped = Timestamps.Stamp(Feature(Entry()), Feature(Entry()) with { Status = FeatureStatus.Shipped }, Now);

        Assert.That(stamped.Updated, Is.EqualTo(Now));
        Assert.That(stamped.Entries.Single().Updated, Is.EqualTo(Edited));
    }

    [Test]
    public void EntryEditBumpsOnlyTheEntry()
    {
        var stamped = Timestamps.Stamp(Feature(Entry()), Feature(Entry() with { Summary = "Round once" }), Now);

        Assert.That(stamped.Updated, Is.EqualTo(Edited));
        Assert.That(stamped.Entries.Single().Updated, Is.EqualTo(Now));
    }

    [Test]
    public void UnchangedFeatureIsReturnedAsIs()
    {
        var current = Feature(Entry());

        Assert.That(Timestamps.Stamp(Feature(Entry()), current, Now), Is.SameAs(current));
    }

    [Test]
    public void ReadOnTheSameDayChangesNothing()
    {
        var entry = Entry();

        Assert.That(Timestamps.MarkRead(entry, new DateOnly(2026, 10, 22)), Is.SameAs(entry));
    }

    [Test]
    public void ReadOnANewDayWritesTheDateAndLeavesUpdatedAlone()
    {
        var read = Timestamps.MarkRead(Entry(), new DateOnly(2026, 10, 26));

        Assert.That(read.Read, Is.EqualTo(new DateOnly(2026, 10, 26)));
        Assert.That(read.Updated, Is.EqualTo(Edited));
    }

    [Test]
    public void FirstReadWritesTheDate()
    {
        Assert.That(Timestamps.MarkRead(Entry() with { Read = null }, new DateOnly(2026, 10, 26)).Read, Is.EqualTo(new DateOnly(2026, 10, 26)));
    }
}
