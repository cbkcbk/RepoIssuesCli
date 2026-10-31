using RepoInfraCli.Features.Infra.Postgres;

namespace RepoInfraCli.Tests.Postgres;

public class BackupDumpsTests
{
    [Test]
    public void ParseOrdersByTheKeyTimestampAndFallsBackToLastModified()
    {
        var dumps = BackupDumps.Parse("""
                                      db/shop/shop-20261030T030000Z.dump	300	2026-10-30T03:00:09.000Z
                                      db/shop/manual.dump	7	2026-10-29T10:00:00.000Z
                                      db/shop/shop-20261028T030000Z.dump	100	2026-10-31T00:00:00.000Z

                                      """.ReplaceLineEndings("\r\n"));

        Assert.That(dumps.Select(dump => dump.Key), Is.EqualTo(new[] { "db/shop/shop-20261028T030000Z.dump", "db/shop/manual.dump", "db/shop/shop-20261030T030000Z.dump" }));
        Assert.That(dumps[0].Time, Is.EqualTo(new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero)));
        Assert.That(dumps[1].Time, Is.EqualTo(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero)));
        Assert.That(dumps[2].Size, Is.EqualTo(300));
    }

    [TestCase("2026-10-30T05:00+02:00", "2026-10-30T03:00:00Z")]
    [TestCase("2026-10-30", "2026-10-30T00:00:00Z")]
    [TestCase("20261030T030000Z", "2026-10-30T03:00:00Z")]
    public void TimesWithoutOffsetAreUtc(string text, string expected)
    {
        Assert.That(BackupDumps.ParseTime(text), Is.EqualTo(DateTimeOffset.Parse(expected)));
    }

    [TestCase(512, "512 B")]
    [TestCase(1536, "1.5 KB")]
    [TestCase(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void SizesAreHumanReadable(long size, string expected)
    {
        Assert.That(new BackupDump("k", size, DateTimeOffset.UnixEpoch).SizeText, Is.EqualTo(expected));
    }

    [Test]
    public void SelectChoosesNewestOrAtOrExactKey()
    {
        var dumps = BackupDumps.Parse("a/x-20261028T030000Z.dump\t1\t\na/x-20261029T030000Z.dump\t1\t\n");

        Assert.That(BackupDumps.Select(dumps, "x", "b/a/", null, null).Key, Is.EqualTo("a/x-20261029T030000Z.dump"));
        Assert.That(BackupDumps.Select(dumps, "x", "b/a/", null, new DateTimeOffset(2026, 10, 29, 2, 59, 59, TimeSpan.Zero)).Key, Is.EqualTo("a/x-20261028T030000Z.dump"));
        Assert.That(BackupDumps.Select(dumps, "x", "b/a/", "a/x-20261028T030000Z.dump", null).Key, Is.EqualTo("a/x-20261028T030000Z.dump"));
        Assert.Throws<BackupNotFoundException>(() => BackupDumps.Select([], "x", "b/a/", null, null));
    }
}
