using System.Text.Json;

namespace RepoDocsCli.Tests.Cli;

public class InitCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    string[] DocsFiles() =>
        Directory.EnumerateFiles(repo.Folder).Select(Path.GetFileName).Where(name => name != FolderLock.FileName).Order(StringComparer.Ordinal).ToArray()!;

    [Test]
    public void InitCreatesRepoConfigAndIgnoreFiles()
    {
        var result = repo.Run("init");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(DocsFiles(), Is.EqualTo(new[] { ".gitignore", "config.yaml", "repo.yaml" }));
        Assert.That(repo.ReadDocsFile(".gitignore"), Is.EqualTo(".lock\n"));
        Assert.That(repo.ReadDocsFile("repo.yaml"), Is.EqualTo("entries: []\n"));
        Assert.That(repo.ReadDocsFile("config.yaml"), Does.StartWith("version: 1\nkinds:\n  rule:\n    render: index\n"));
        Assert.That(result.Output, Is.EqualTo($"Initialized {repo.Folder}\n"));
    }

    [Test]
    public void InitIsIdempotent()
    {
        repo.Subfolder("App", "Features", "Billing");
        repo.Run("init");
        var before = DocsFiles().ToDictionary(name => name, repo.ReadDocsFile);

        var second = repo.Run("init");

        Assert.That(second.ExitCode, Is.Zero);
        Assert.That(second.Output, Is.EqualTo($"Already initialized: {repo.Folder}\n"));
        Assert.That(DocsFiles().ToDictionary(name => name, repo.ReadDocsFile), Is.EqualTo(before));
    }

    [Test]
    public void InitRestoresMissingFilesButDoesNotPrefillAgain()
    {
        repo.Run("init");
        File.Delete(Path.Combine(repo.Folder, "config.yaml"));
        repo.Subfolder("App", "Features", "Billing");

        var result = JsonDocument.Parse(repo.Run("init", "--json").Output).RootElement;

        Assert.That(result.GetProperty("created").GetBoolean(), Is.False);
        Assert.That(result.GetProperty("files").EnumerateArray().Select(file => file.GetString()), Is.EqualTo(new[] { ".repo-docs/config.yaml" }));
        Assert.That(result.GetProperty("features").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void InitPrefillsOneFeaturePerFeaturesFolder()
    {
        repo.Subfolder("App", "Features", "Billing");
        repo.Subfolder("App", "Features", "UserAccounts");
        repo.Subfolder("Core", "Features", "Billing");
        repo.Subfolder("Core", "Features", "Repo");
        repo.Subfolder("App", "bin", "Features", "Ignored");
        repo.Subfolder(".hidden", "Features", "Ignored");

        var result = repo.Run("init");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(DocsFiles(), Is.EqualTo(new[] { ".gitignore", "billing.yaml", "config.yaml", "repo-feature.yaml", "repo.yaml", "user-accounts.yaml" }));
        Assert.That(repo.ReadDocsFile("billing.yaml"), Does.Match(
            """
            ^feature: billing
            status: shipped
            when:
              paths:
                - App/Features/Billing/
                - Core/Features/Billing/
            created: \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ
            entries: \[\]
            $
            """.ReplaceLineEndings("\n")));
        Assert.That(result.Output, Does.Contain("Added feature billing (App/Features/Billing/, Core/Features/Billing/)"));
    }

    [Test]
    public void InitJsonReportsFolderCreatedFilesAndFeatures()
    {
        repo.Subfolder("Src", "Features", "Search");

        var result = repo.Run("init", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;
        var feature = json.GetProperty("features")[0];

        Assert.That(result.Errors, Is.Empty);
        Assert.That(json.GetProperty("folder").GetString(), Is.EqualTo(repo.Folder));
        Assert.That(json.GetProperty("created").ValueKind, Is.EqualTo(JsonValueKind.True));
        Assert.That(json.GetProperty("files").EnumerateArray().Select(file => file.GetString()),
            Is.EquivalentTo(new[] { ".repo-docs/.gitignore", ".repo-docs/repo.yaml", ".repo-docs/config.yaml", ".repo-docs/search.yaml" }));
        Assert.That(feature.GetProperty("feature").GetString(), Is.EqualTo("search"));
        Assert.That(feature.GetProperty("status").GetString(), Is.EqualTo("shipped"));
        Assert.That(feature.GetProperty("when").GetProperty("paths")[0].GetString(), Is.EqualTo("Src/Features/Search/"));
        Assert.That(feature.GetProperty("entries").GetArrayLength(), Is.Zero);
        Assert.That(feature.TryGetProperty("summary", out _), Is.False);
        Assert.That(feature.GetProperty("created").GetDateTimeOffset().Offset, Is.EqualTo(TimeSpan.Zero));

        var again = JsonDocument.Parse(repo.Run("init", "--json").Output).RootElement;
        Assert.That(again.GetProperty("created").ValueKind, Is.EqualTo(JsonValueKind.False));
    }

    [Test]
    public void InitGitAddsCreatedFiles()
    {
        repo.InitGit();
        repo.Subfolder("Src", "Features", "Search");

        repo.Run("init");

        var staged = repo.Git("diff", "--cached", "--name-only").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(staged, Is.EquivalentTo(new[] { ".repo-docs/.gitignore", ".repo-docs/config.yaml", ".repo-docs/repo.yaml", ".repo-docs/search.yaml" }));
    }

    [Test]
    public void RootOptionChoosesTheRepository()
    {
        var other = repo.Subfolder("elsewhere");

        var result = repo.RunWithoutRoot("init", "--root", other);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(File.Exists(Path.Combine(other, ".repo-docs", "repo.yaml")), Is.True);
    }

    [Test]
    public void InitFromASubfolderUsesTheRepositoryRoot()
    {
        repo.InitGit();
        var nested = repo.Subfolder("src", "deep");

        var result = repo.RunIn(nested, "init");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(File.Exists(Path.Combine(repo.Folder, "repo.yaml")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(nested, ".repo-docs")), Is.False);
    }

    [Test]
    public void UnknownCommandIsAUsageErrorOnStderr()
    {
        var result = repo.Run("nope");

        Assert.That(result.ExitCode, Is.EqualTo(ExitCodes.Usage));
        Assert.That(result.Output, Is.Empty);
    }
}
