using System.Text.Json;
using RepoInfraCli.Features.Infra;

namespace RepoInfraCli.Tests.Cli;

public class InitCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    string[] StubFiles() =>
        [.. Directory.EnumerateFiles(repo.Workspace.StubsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).OfType<string>()];

    [Test]
    public void InitScaffoldsEntryLuauRcAndStubs()
    {
        var result = repo.Run("init");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.StartWith($"Initialized {repo.Folder} for project infra-tests-"));
        Assert.That(repo.ReadFile(".repo-infra/infra.luau"), Does.Contain("local infra = require(\"@infra\")").And.Contain("infra.project \"infra-tests-"));
        Assert.That(repo.ReadFile(".repo-infra/.luaurc"), Does.Contain("\"infra\": \".luau/@infra\"").And.Contain("\"languageMode\": \"strict\""));
        Assert.That(StubFiles(), Is.EqualTo(new[]
        {
            ".luaurc", "cloudflare.luau", "compose.luau", "digitalocean.luau", "dotnet.luau", "init.luau", "postgres.luau", "repo.luau", "traefik.luau", "types.luau",
        }));
        Assert.That(repo.ReadFile(".repo-infra/.luau/@infra/init.luau"), Does.Contain("function infra.host(name: string)"));
    }

    [Test]
    public void FreshInitValidates()
    {
        repo.Run("init");

        var result = repo.Run("validate");

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output, Is.EqualTo("No problems found.\n"));
    }

    [Test]
    public void InitIsIdempotentAndNeverOverwritesInfraLuau()
    {
        repo.Run("init");
        repo.WriteInfra("-- mine\n");

        var second = repo.Run("init");

        Assert.That(second.ExitCode, Is.Zero);
        Assert.That(second.Output, Is.EqualTo($"Already initialized: {repo.Folder}\n"));
        Assert.That(repo.ReadFile(".repo-infra/infra.luau"), Is.EqualTo("-- mine\n"));
    }

    [Test]
    public void RerunRefreshesChangedOrMissingStubs()
    {
        repo.Run("init");
        repo.WriteFile(".repo-infra/.luau/@infra/compose.luau", "stale");
        File.Delete(Path.Combine(repo.Workspace.StubsFolder, "repo.luau"));

        var json = JsonDocument.Parse(repo.Run("init", "--json").Output).RootElement;

        Assert.That(json.GetProperty("created").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("files").GetArrayLength(), Is.Zero);
        Assert.That(json.GetProperty("stubs").EnumerateArray().Select(stub => stub.GetString()),
            Is.EquivalentTo(new[] { ".repo-infra/.luau/@infra/compose.luau", ".repo-infra/.luau/@infra/repo.luau" }));
        Assert.That(repo.ReadFile(".repo-infra/.luau/@infra/compose.luau"), Is.EqualTo(InfraScaffold.ReadStub("compose.luau")));
    }

    [Test]
    public void InitJsonListsCreatedFiles()
    {
        var result = repo.Run("init", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;

        Assert.That(result.Errors, Is.Empty);
        Assert.That(json.GetProperty("folder").GetString(), Is.EqualTo(repo.Folder));
        Assert.That(json.GetProperty("created").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("project").GetString(), Does.StartWith("infra-tests-"));
        Assert.That(json.GetProperty("files").EnumerateArray().Select(file => file.GetString()),
            Is.EqualTo(new[] { ".repo-infra/infra.luau", ".repo-infra/.luaurc" }));
        Assert.That(json.GetProperty("stubs").GetArrayLength(), Is.EqualTo(10));
    }

    [Test]
    public void ShippedStubsMatchTheSourceStubs()
    {
        var source = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(TempRepo.DemoEntry)))!, "Luau", "@infra");

        foreach (var name in InfraScaffold.StubNames)
            Assert.That(InfraScaffold.ReadStub(name), Is.EqualTo(File.ReadAllText(Path.Combine(source, name))), name);
        Assert.That(InfraScaffold.StubNames, Has.Count.EqualTo(Directory.GetFiles(source).Length));
    }

    [TestCase("My Repo", "my-repo")]
    [TestCase("snappy", "snappy")]
    [TestCase("__", "app")]
    public void ProjectNameComesFromTheRootFolder(string folder, string expected) =>
        Assert.That(InfraScaffold.ProjectName(Path.Combine(Path.GetTempPath(), folder)), Is.EqualTo(expected));
}
