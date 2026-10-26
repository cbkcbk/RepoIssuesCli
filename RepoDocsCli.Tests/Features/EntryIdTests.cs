using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class EntryIdTests
{
    [TestCase("Round half to even", "round-half-to-even")]
    [TestCase("Don't cache VAT rates!", "dont-cache-vat-rates")]
    [TestCase("  Use `MidpointRounding.ToEven` -- always ", "use-midpointrounding-toeven-always")]
    [TestCase("The invoice total is computed from line items and rounded once at the end", "the-invoice-total-is-computed-from-line")]
    [TestCase("!!!", "entry")]
    [TestCase("Ærlig talt", "rlig-talt")]
    public void SummariesBecomeShortKebabCaseIds(string summary, string expected)
    {
        var id = EntryId.FromSummary(summary, []);

        Assert.That(id, Is.EqualTo(expected));
        Assert.That(EntryId.IsValid(id), Is.True);
        Assert.That(id, Has.Length.LessThanOrEqualTo(EntryId.MaxDerivedLength));
    }

    [Test]
    public void DerivedIdsAreUniqueWithinTheOwner() =>
        Assert.That(EntryId.FromSummary("Round once", ["round-once", "round-once-2"]), Is.EqualTo("round-once-3"));
}
