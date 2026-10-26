using RepoDocsCli.Features.Queries;

namespace RepoDocsCli.Tests.Features;

public class PathMatchTests
{
    [TestCase("App/Billing/", "App/Billing/Invoice.cs", true)]
    [TestCase("App/Billing/", "App/Billing", true)]
    [TestCase("App/Billing", "App/Billing/Invoice.cs", true)]
    [TestCase("App/Billing", "App/BillingOld/Invoice.cs", false)]
    [TestCase("App/Billing/Invoice.cs", "App/Billing/Invoice.cs", true)]
    [TestCase("App/Billing/Invoice.cs", "App/Billing/Invoice.csx", false)]
    [TestCase("./App\\Billing/", "App/Billing/Invoice.cs", true)]
    [TestCase("*.cs", "App/Billing/Invoice.cs", true)]
    [TestCase("*.cs", "App/Billing/Invoice.md", false)]
    [TestCase("App/*/Invoice.cs", "App/Billing/Invoice.cs", true)]
    [TestCase("App/*/Invoice.cs", "App/Billing/Sub/Invoice.cs", false)]
    [TestCase("App/**/Invoice.cs", "App/Billing/Sub/Invoice.cs", true)]
    [TestCase("App/**/Invoice.cs", "App/Invoice.cs", true)]
    [TestCase("App/**", "App/Billing/Invoice.cs", true)]
    [TestCase("App/*/", "App/Billing/Invoice.cs", true)]
    [TestCase("", "App/Billing/Invoice.cs", false)]
    public void Covers(string hint, string path, bool expected)
    {
        Assert.That(PathMatch.Covers(hint, path), Is.EqualTo(expected));
    }

    [Test]
    public void RepoRelativeRejectsPathsOutsideTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo-root");

        Assert.That(DocsFor.RepoRelative(root, "src/a.cs", Path.GetTempPath()), Is.EqualTo("src/a.cs"));
        Assert.That(DocsFor.RepoRelative(root, root, Path.GetTempPath()), Is.EqualTo(""));
        Assert.Throws<UsageException>(() => DocsFor.RepoRelative(root, "../other/a.cs", root));
    }
}
