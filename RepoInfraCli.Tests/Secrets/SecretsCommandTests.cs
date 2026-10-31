using System.Text.Json;
using RepoInfraCli.Cli;
using RepoInfraCli.Cli.Commands.Secrets;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Tests.Secrets;

public class SecretsCommandTests
{
    SecretsRepo secrets = null!;

    [SetUp]
    public void SetUp() => secrets = new SecretsRepo();

    [TearDown]
    public void TearDown() => secrets.Dispose();

    static JsonElement Json(RunResult result) => JsonDocument.Parse(result.Output).RootElement;

    void Init(string name = "me") => Assert.That(secrets.Run("secrets", "init", "--name", name).ExitCode, Is.Zero);

    [Test]
    public void InitCreatesKeyAndFileAndAddsSelf()
    {
        var result = secrets.Run("secrets", "init", "--name", "me", "--json");
        var json = Json(result);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(File.ReadAllText(secrets.KeyFile), Does.StartWith(SecretKey.PrivatePrefix));
        Assert.That(json.GetProperty("keyCreated").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("fileCreated").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("recipientAdded").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("file").GetString(), Is.EqualTo(".repo-infra/secrets.yaml"));
        Assert.That(secrets.Store.Recipients["me"], Is.EqualTo(json.GetProperty("publicKey").GetString()));
        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(secrets.KeyFile), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [Test]
    public void InitIsIdempotent()
    {
        Init();
        var before = secrets.StoreText;
        var key = File.ReadAllText(secrets.KeyFile);

        var json = Json(secrets.Run("secrets", "init", "--name", "other", "--json"));

        Assert.That(json.GetProperty("keyCreated").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("recipientAdded").GetBoolean(), Is.False);
        Assert.That(json.GetProperty("recipient").GetString(), Is.EqualTo("me"));
        Assert.That(secrets.StoreText, Is.EqualTo(before));
        Assert.That(File.ReadAllText(secrets.KeyFile), Is.EqualTo(key));
    }

    [Test]
    public void InitWithRepoInfraKeyDoesNotWriteAKeyFile()
    {
        using var ci = SecretKey.Generate();
        secrets.Variables[SecretEnvironment.KeyVariable] = ci.Export();

        var json = Json(secrets.Run("secrets", "init", "--name", "ci", "--json"));

        Assert.That(File.Exists(secrets.KeyFile), Is.False);
        Assert.That(json.GetProperty("keySource").GetString(), Is.EqualTo(SecretEnvironment.KeyVariable));
        Assert.That(secrets.Store.Recipients["ci"], Is.EqualTo(ci.PublicKey));
    }

    [Test]
    public void SetReadsStdinAndShowRevealsIt()
    {
        Init();

        var set = secrets.RunWithInput("p@ss word\n", "secrets", "set", "db/password", "--stdin");
        var show = secrets.Run("secrets", "show", "db/password");

        Assert.That(set.ExitCode, Is.Zero, set.Errors);
        Assert.That(set.Output, Does.Not.Contain("p@ss"));
        Assert.That(secrets.StoreText, Does.Contain("db/password: v1:").And.Not.Contain("p@ss"));
        Assert.That(show.Output, Is.EqualTo("p@ss word\n"));
    }

    [Test]
    public void SetReadsAHiddenPrompt()
    {
        Init();

        var set = secrets.RunInteractive("typed-value", "secrets", "set", "api/token");

        Assert.That(set.ExitCode, Is.Zero, set.Errors);
        Assert.That(set.Output, Does.Not.Contain("typed-value"));
        Assert.That(Json(secrets.Run("secrets", "show", "api/token", "--json")).GetProperty("value").GetString(), Is.EqualTo("typed-value"));
    }

    [Test]
    public void SetRefusesAValueOnTheCommandLine()
    {
        Init();
        var before = secrets.StoreText;

        var result = secrets.Run("secrets", "set", "db/password", "hunter2");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(secrets.StoreText, Is.EqualTo(before));
    }

    [Test]
    public void SetWithoutStdinOrTerminalIsAUsageError()
    {
        Init();

        var result = secrets.Run("secrets", "set", "db/password");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--stdin"));
    }

    [Test]
    public void SetRejectsEmptyValuesBadNamesEnvOptionAndMissingStore()
    {
        Assert.That(secrets.RunWithInput("x", "secrets", "set", "a", "--stdin").Errors, Does.Contain("infra secrets init"));
        Init();

        Assert.That(secrets.RunWithInput("\n", "secrets", "set", "a", "--stdin").ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(secrets.RunWithInput("x", "secrets", "set", "bad name", "--stdin").ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(secrets.RunWithInput("x", "secrets", "set", "a", "--stdin", "--env", "prod").Errors, Does.Contain("prod/db/password"));
    }

    [Test]
    public void SetChangesOnlyThatLine()
    {
        Init();
        secrets.RunWithInput("1", "secrets", "set", "a", "--stdin");
        secrets.RunWithInput("2", "secrets", "set", "b", "--stdin");
        secrets.RunWithInput("3", "secrets", "set", "c", "--stdin");
        var before = secrets.StoreText.Split('\n');

        secrets.RunWithInput("22", "secrets", "set", "b", "--stdin");
        var after = secrets.StoreText.Split('\n');

        var changed = Enumerable.Range(0, before.Length).Where(index => before[index] != after[index]).ToList();
        Assert.That(after, Has.Length.EqualTo(before.Length));
        Assert.That(changed, Has.Count.EqualTo(1));
        Assert.That(after[changed[0]], Does.StartWith("  b: v1:"));
    }

    [Test]
    public void GenerateStoresAUrlSafeValueAndRefusesToOverwrite()
    {
        Init();

        var generated = secrets.Run("secrets", "generate", "postgres/api", "--length", "40", "--json");
        var value = Json(secrets.Run("secrets", "show", "postgres/api", "--json")).GetProperty("value").GetString()!;
        var again = secrets.Run("secrets", "generate", "postgres/api");
        var forced = secrets.Run("secrets", "generate", "postgres/api", "--force");

        Assert.That(generated.ExitCode, Is.Zero, generated.Errors);
        Assert.That(Json(generated).GetProperty("created").GetBoolean(), Is.True);
        Assert.That(generated.Output, Does.Not.Contain(value));
        Assert.That(value, Has.Length.EqualTo(40));
        Assert.That(value.All(SecretsGenerateCommand.Alphabet.Contains), Is.True);
        Assert.That(again.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(again.Errors, Does.Contain("--force"));
        Assert.That(forced.ExitCode, Is.Zero);
        Assert.That(secrets.Run("secrets", "show", "postgres/api").Output.Trim(), Is.Not.EqualTo(value));
    }

    [Test]
    public void GenerateRejectsOutOfRangeLengths()
    {
        Init();

        Assert.That(secrets.Run("secrets", "generate", "a", "--length", "4").ExitCode, Is.EqualTo(InfraExitCodes.Usage));
    }

    [Test]
    public void ShowReportsMissingAndUnreadableSecrets()
    {
        Init();
        using var stranger = SecretKey.Generate();
        var store = secrets.Store;
        store.Secrets["theirs"] = SecretCipher.Encrypt("theirs", "x", [stranger.PublicKey]);
        store.Save(secrets.Repo.Workspace);

        var missing = secrets.Run("secrets", "show", "nope");
        var unreadable = secrets.Run("secrets", "show", "theirs");

        Assert.That(missing.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Assert.That(missing.Errors, Does.Contain("infra secrets set nope"));
        Assert.That(unreadable.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(unreadable.Errors, Does.Contain("cannot decrypt"));
    }

    [Test]
    public void ShowDetectsAValueCopiedFromAnotherName()
    {
        Init();
        secrets.RunWithInput("staging", "secrets", "set", "staging/db", "--stdin");
        var store = secrets.Store;
        store.Secrets["prod/db"] = store.Secrets["staging/db"];
        store.Save(secrets.Repo.Workspace);

        var result = secrets.Run("secrets", "show", "prod/db");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Errors, Does.Contain("failed authentication"));
        Assert.That(result.Output, Does.Not.Contain("staging"));
    }

    [Test]
    public void KeyPrintsThisMachinesPublicKey()
    {
        Assert.That(secrets.Run("secrets", "key").ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
        Init();

        var text = secrets.Run("secrets", "key");
        var json = Json(secrets.Run("secrets", "key", "--json"));

        Assert.That(text.Output, Is.EqualTo(secrets.Store.Recipients["me"] + "\n"));
        Assert.That(json.GetProperty("recipient").GetString(), Is.EqualTo("me"));
        Assert.That(json.GetProperty("source").GetString(), Is.EqualTo(secrets.KeyFile));
    }

    [Test]
    public void RecipientsAddSharesExistingSecrets()
    {
        Init();
        secrets.RunWithInput("v", "secrets", "set", "a", "--stdin");
        using var ci = SecretKey.Generate();

        var result = secrets.Run("secrets", "recipients", "add", "ci", ci.PublicKey, "--json");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(Json(result).GetProperty("shared").EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { "a" }));
        Assert.That(secrets.Store.Reveal("a", ci).Value, Is.EqualTo("v"));
    }

    [Test]
    public void RecipientsAddRejectsBadKeys()
    {
        Init();

        var result = secrets.Run("secrets", "recipients", "add", "ci", "p256:nope");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
    }

    [Test]
    public void RecipientsRemoveListsSecretsToRotate()
    {
        Init();
        using var ci = SecretKey.Generate();
        secrets.Run("secrets", "recipients", "add", "ci", ci.PublicKey);
        secrets.RunWithInput("v", "secrets", "set", "a", "--stdin");

        var result = secrets.Run("secrets", "recipients", "remove", "ci");
        var missing = secrets.Run("secrets", "recipients", "remove", "ci");

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(result.Output, Does.Contain("Rotate").And.Contain("infra secrets set a"));
        Assert.That(secrets.Store.Reveal("a", ci).Status, Is.EqualTo(SecretRevealStatus.NotRecipient));
        Assert.That(secrets.Run("secrets", "show", "a").Output, Is.EqualTo("v\n"));
        Assert.That(missing.ExitCode, Is.EqualTo(InfraExitCodes.NotFound));
    }

    [Test]
    public void ListShowsReadersNeverValuesAndDoesNotRewriteTheFile()
    {
        Init();
        using var ci = SecretKey.Generate();
        secrets.RunWithInput("first-value", "secrets", "set", "a", "--stdin");
        secrets.Run("secrets", "recipients", "add", "ci", ci.PublicKey);
        secrets.RunWithInput("second-value", "secrets", "set", "b", "--stdin");
        var path = Path.Combine(secrets.Repo.Folder, SecretStore.FileName);
        var before = secrets.StoreText;
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var text = secrets.Run("secrets", "list");
        var json = Json(secrets.Run("secrets", "list", "--json"));

        Assert.That(text.ExitCode, Is.Zero, text.Errors);
        Assert.That(text.Output, Does.Contain("me (this machine)").And.Contain("ci").And.Not.Contain("first-value").And.Not.Contain("second-value"));
        Assert.That(json.GetProperty("secrets")[1].GetProperty("readers").EnumerateArray().Select(item => item.GetString()), Is.EqualTo(new[] { "ci", "me" }));
        Assert.That(json.GetProperty("secrets")[0].GetProperty("readable").GetBoolean(), Is.True);
        Assert.That(secrets.StoreText, Is.EqualTo(before));
        Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void ListFlagsMissingAndUnreferencedSecrets()
    {
        secrets.Repo.WriteScript("""
            local sudo = infra.secret "hosts/web/sudo"
            local token = infra.secret "api/token"
            """);
        Init();
        secrets.RunWithInput("x", "secrets", "set", "api/token", "--stdin");
        secrets.RunWithInput("x", "secrets", "set", "old/thing", "--stdin");

        var json = Json(secrets.Run("secrets", "list", "--json"));
        var entries = json.GetProperty("secrets").EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("referenced").GetBoolean());

        Assert.That(entries, Is.EqualTo(new Dictionary<string, bool> { ["api/token"] = true, ["old/thing"] = false }));
        Assert.That(json.GetProperty("missing")[0].GetProperty("path").GetString(), Is.EqualTo("hosts/web/sudo"));
        Assert.That(json.GetProperty("missing")[0].GetProperty("command").GetString(), Is.EqualTo("infra secrets set hosts/web/sudo"));
    }

    [Test]
    public void ListChecksEveryEnvAndUsesEnvScopedEntries()
    {
        secrets.Repo.WriteScript("""
            local token = infra.secret "api/token"
            """);
        secrets.Repo.WriteFile(".repo-infra/envs/prod/.keep", "");
        secrets.Repo.WriteFile(".repo-infra/envs/staging/.keep", "");
        Init();
        secrets.RunWithInput("x", "secrets", "set", "prod/api/token", "--stdin");

        var json = Json(secrets.Run("secrets", "list", "--json"));
        var missing = json.GetProperty("missing");

        Assert.That(missing.GetArrayLength(), Is.EqualTo(1));
        Assert.That(missing[0].GetProperty("env").GetString(), Is.EqualTo("staging"));
        Assert.That(missing[0].GetProperty("command").GetString(), Is.EqualTo("infra secrets set staging/api/token"));
        Assert.That(json.GetProperty("secrets")[0].GetProperty("referenced").GetBoolean(), Is.True);
    }

    [Test]
    public void ListToleratesEvaluationErrors()
    {
        secrets.Repo.WriteScript("error('boom')\n");
        Init();
        secrets.RunWithInput("x", "secrets", "set", "a", "--stdin");

        var result = secrets.Run("secrets", "list", "--json");
        var json = Json(result);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        Assert.That(json.GetProperty("notes")[0].GetString(), Does.Contain("did not evaluate"));
        Assert.That(json.GetProperty("secrets")[0].TryGetProperty("referenced", out _), Is.False);
    }
}
