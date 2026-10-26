using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class FeatureIdTests
{
    [TestCase("Issues", "issues")]
    [TestCase("UserAccounts", "user-accounts")]
    [TestCase("HTTPClient", "http-client")]
    [TestCase("Auth2Factor", "auth2-factor")]
    [TestCase("My_Feature.v2", "my-feature-v2")]
    [TestCase("Repo", "repo-feature")]
    [TestCase("Config", "config-feature")]
    public void FolderNamesBecomeKebabCaseIds(string folder, string expected) =>
        Assert.That(FeatureId.FromFolderName(folder), Is.EqualTo(expected));

    [TestCase("billing", true)]
    [TestCase("user-accounts", true)]
    [TestCase("Billing", false)]
    [TestCase("double--hyphen", false)]
    [TestCase("-leading", false)]
    [TestCase("repo", false)]
    [TestCase("config", false)]
    [TestCase("", false)]
    public void ValidIdsAreLowercaseKebabCaseAndNotReserved(string id, bool valid) =>
        Assert.That(FeatureId.IsValid(id), Is.EqualTo(valid));
}
