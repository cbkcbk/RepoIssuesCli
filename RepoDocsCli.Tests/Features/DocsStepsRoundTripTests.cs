using RepoCli.Core.Features.IssueLinks;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class DocsStepsRoundTripTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    static FeatureEntry Plan(string id, params PlanStep[] steps) => new()
    {
        Id = id,
        Kind = "plan",
        Summary = "Roll out invoices",
        Steps = [.. steps],
        Body = "Ship behind a flag.\n",
    };

    [Test]
    public void FilesWrittenByTheStoreStillLoadAfterACoreRewrite()
    {
        var store = repo.Store;
        store.Initialize();
        store.Save(new Feature
        {
            Id = "billing",
            Status = FeatureStatus.InProgress,
            Entries = [Plan("rollout", new PlanStep { Title = "Schema and store", Issues = [23, 24] }, new PlanStep { Title = "Cut over", Issues = [24] }, new PlanStep { Title = "Guide" })],
        });
        store.SaveRepo(new RepoDocs { Entries = [Plan("cleanup", new PlanStep { Title = "Tidy", Issues = [24, 7] })] });
        var before = repo.ReadDocsFile("billing.yaml");

        var steps = new DocsSteps(repo.Root);
        DocsReferenceScan result;
        using (steps.Lock())
            result = steps.Renumber(24, 1024);

        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.References.Select(reference => reference.Address), Is.EqualTo(new[] { "billing/rollout", "billing/rollout", "repo/cleanup" }));

        var snapshot = store.Load();
        Assert.That(snapshot.Errors, Is.Empty);
        var billing = snapshot.Find("billing")!;
        Assert.That(billing.Entries.Single().Steps.Select(step => step.Issues), Is.EqualTo(new[] { new[] { 23, 1024 }, new[] { 1024 }, Array.Empty<int>() }));
        Assert.That(snapshot.Repo.Entries.Single().Steps.Single().Issues, Is.EqualTo(new[] { 1024, 7 }));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Is.EqualTo(before.Replace("[23, 24]", "[23, 1024]").Replace("[24]", "[1024]")));
        Assert.That(DocsYaml.Serialize(billing), Is.EqualTo(repo.ReadDocsFile("billing.yaml")));
    }
}
