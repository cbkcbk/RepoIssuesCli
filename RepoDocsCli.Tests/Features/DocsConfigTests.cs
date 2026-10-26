using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Tests.Features;

public class DocsConfigTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    [Test]
    public void InitWritesTheDefaultKinds()
    {
        repo.Store.Initialize();

        Assert.That(repo.ReadDocsFile("config.yaml"), Is.EqualTo(DefaultConfigYaml.ReplaceLineEndings("\n")));
        Assert.That(repo.Store.Load().Config, Is.EqualTo(DocsConfig.Default()).Using<DocsConfig>(SameYaml));
    }

    [Test]
    public void CustomKindsRoundTrip()
    {
        var config = new DocsConfig
        {
            Kinds = new Dictionary<string, KindConfig>
            {
                ["rule"] = new() { Render = RenderMode.Always, Push = PushPolicy.OnEdit },
                ["runbook"] = new() { Render = RenderMode.Index, Push = PushPolicy.WhenFeatureActive, Prune = new PrunePolicy { UnreadDays = 30 } },
            },
        };
        repo.Store.SaveConfig(config);

        var loaded = repo.Store.Load();

        Assert.That(loaded.Errors, Is.Empty);
        Assert.That(loaded.Config.Kinds["runbook"].Push, Is.EqualTo(PushPolicy.WhenFeatureActive));
        Assert.That(loaded.Config.Kinds["runbook"].Prune?.UnreadDays, Is.EqualTo(30));
        Assert.That(DocsYaml.Serialize(loaded.Config), Is.EqualTo(repo.ReadDocsFile("config.yaml")));
        Assert.That(repo.ReadDocsFile("config.yaml"), Does.Contain("  runbook:\n    render: index\n    push: when-feature-active\n    prune:\n      unreadDays: 30\n"));
    }

    [Test]
    public void EmptyKindUsesConservativeDefaults()
    {
        repo.WriteDocsFile("config.yaml", "version: 1\nkinds:\n  note: {}\n  todo:\n");

        var config = repo.Store.Load().Config;

        Assert.That(config.Kinds.Keys, Is.EqualTo(new[] { "note", "todo" }));
        Assert.That(config.Kinds.Values, Has.All.Matches<KindConfig>(kind =>
            kind.Render == RenderMode.Never && kind.Push == PushPolicy.Never && kind.Prune == null));
    }

    [TestCase("version: 1\nkinds:\n  rule:\n    render: sometimes\n")]
    [TestCase("version: 1\nkinds:\n  rule:\n    colour: red\n")]
    [TestCase("version: 1\nkinds:\n  rule:\n    prune:\n      unreadDays: soon\n")]
    public void InvalidKindConfigIsAFileError(string content)
    {
        repo.WriteDocsFile("config.yaml", content);

        var snapshot = repo.Store.Load();

        Assert.That(snapshot.Errors.Single().FileName, Is.EqualTo("config.yaml"));
        Assert.That(snapshot.Config.Kinds.Keys, Is.EquivalentTo(DocsConfig.Default().Kinds.Keys));
    }

    static bool SameYaml(DocsConfig left, DocsConfig right) => DocsYaml.Serialize(left) == DocsYaml.Serialize(right);

    const string DefaultConfigYaml = """
        version: 1
        kinds:
          rule:
            render: index
            push: on-edit
          fact:
            render: never
            push: on-read
            prune:
              unreadDays: 180
          gotcha:
            render: never
            push: on-edit
            prune:
              unreadDays: 365
          howto:
            render: index
            push: never
            prune:
              unreadDays: 180
          decision:
            render: never
            push: never
          plan:
            render: never
            push: when-feature-active

        """;
}
