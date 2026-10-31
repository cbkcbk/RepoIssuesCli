using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Tests.Secrets;

public class SecretResolverTests
{
    SecretsRepo secrets = null!;
    SecretKey key = null!;

    [SetUp]
    public void SetUp()
    {
        secrets = new SecretsRepo();
        Assert.That(secrets.Run("secrets", "init", "--name", "me").ExitCode, Is.Zero);
        key = secrets.LoadKey();
    }

    [TearDown]
    public void TearDown()
    {
        key.Dispose();
        secrets.Dispose();
    }

    SecretResolution Resolve(string? env, params string[] paths) =>
        new ChainedSecretResolver(secrets.Machine).Resolve(secrets.Repo.Workspace, env, paths.Select(path => new SecretRef(path)));

    void Store(params (string Name, string Value)[] entries)
    {
        var store = secrets.Store;
        foreach (var (name, value) in entries)
            store.Set(name, value);
        store.Save(secrets.Repo.Workspace);
    }

    [Test]
    public void ResolvesFromTheFile()
    {
        Store(("db/password", "from-file"));

        var resolution = Resolve(null, "db/password");

        Assert.That(resolution.Values["db/password"], Is.EqualTo("from-file"));
        Assert.That(resolution.IsComplete, Is.True);
    }

    [Test]
    public void EnvScopedEntryWinsOverTheSharedOne()
    {
        Store(("db/password", "shared"), ("prod/db/password", "prod-only"));

        Assert.That(Resolve("prod", "db/password").Values["db/password"], Is.EqualTo("prod-only"));
        Assert.That(Resolve("staging", "db/password").Values["db/password"], Is.EqualTo("shared"));
        Assert.That(Resolve(null, "db/password").Values["db/password"], Is.EqualTo("shared"));
    }

    [Test]
    public void EnvironmentVariablesComeFirstEnvScopedBeforePlain()
    {
        Store(("db/password", "shared"), ("prod/db/password", "prod-only"));
        secrets.Variables["INFRA_SECRET_DB_PASSWORD"] = "plain-var";

        Assert.That(Resolve("prod", "db/password").Values["db/password"], Is.EqualTo("plain-var"));

        secrets.Variables["INFRA_SECRET_PROD_DB_PASSWORD"] = "prod-var";
        Assert.That(Resolve("prod", "db/password").Values["db/password"], Is.EqualTo("prod-var"));
        Assert.That(Resolve("staging", "db/password").Values["db/password"], Is.EqualTo("plain-var"));
    }

    [Test]
    public void EnvironmentVariablesWorkWithoutAStoreOrKey()
    {
        using var bare = new SecretsRepo();
        bare.Variables["INFRA_SECRET_API_TOKEN"] = "t";

        var resolution = new ChainedSecretResolver(bare.Machine).Resolve(bare.Repo.Workspace, null, [new SecretRef("api/token"), new SecretRef("other")]);

        Assert.That(resolution.Values["api/token"], Is.EqualTo("t"));
        Assert.That(resolution.Missing, Is.EqualTo(new[] { "other" }));
        Assert.That(resolution.Undecryptable, Is.Empty);
    }

    [Test]
    public void UndecryptableIsReportedApartFromMissing()
    {
        using var stranger = SecretKey.Generate();
        var store = secrets.Store;
        store.Recipients.Clear();
        store.AddRecipient("stranger", stranger.PublicKey, null);
        store.Set("theirs", "x");
        store.Save(secrets.Repo.Workspace);

        var resolution = Resolve(null, "theirs", "nowhere");

        Assert.That(resolution.Undecryptable, Is.EqualTo(new[] { "theirs" }));
        Assert.That(resolution.Missing, Is.EqualTo(new[] { "nowhere" }));
        Assert.That(resolution.Values, Is.Empty);
        Assert.That(resolution.IsComplete, Is.False);
    }

    [Test]
    public void StoredSecretWithoutAnyKeyIsUndecryptable()
    {
        Store(("db/password", "x"));
        File.Delete(secrets.KeyFile);

        var resolution = Resolve(null, "db/password");

        Assert.That(resolution.Undecryptable, Is.EqualTo(new[] { "db/password" }));
        Assert.That(resolution.Missing, Is.Empty);
    }

    [Test]
    public void EnvScopedEntryTheMachineCannotReadDoesNotFallBackToShared()
    {
        using var stranger = SecretKey.Generate();
        Store(("db/password", "shared"));
        var store = secrets.Store;
        store.Secrets["prod/db/password"] = SecretCipher.Encrypt("prod/db/password", "secret", [stranger.PublicKey]);
        store.Save(secrets.Repo.Workspace);

        Assert.That(Resolve("prod", "db/password").Undecryptable, Is.EqualTo(new[] { "db/password" }));
    }

    [Test]
    public void SwappedValuesAreUndecryptable()
    {
        Store(("a", "1"), ("b", "2"));
        var store = secrets.Store;
        (store.Secrets["a"], store.Secrets["b"]) = (store.Secrets["b"], store.Secrets["a"]);
        store.Save(secrets.Repo.Workspace);

        Assert.That(Resolve(null, "a", "b").Undecryptable, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void RepoInfraKeyVariableTakesPrecedenceOverTheKeyFile()
    {
        using var ci = SecretKey.Generate();
        var store = secrets.Store;
        store.AddRecipient("ci", ci.PublicKey, key);
        store.Secrets["ci-only"] = SecretCipher.Encrypt("ci-only", "pipeline", [ci.PublicKey]);
        store.Save(secrets.Repo.Workspace);

        Assert.That(Resolve(null, "ci-only").Undecryptable, Is.EqualTo(new[] { "ci-only" }));

        secrets.Variables[SecretEnvironment.KeyVariable] = ci.Export();
        using var loaded = secrets.Machine.LoadKey();

        Assert.That(loaded!.PublicKey, Is.EqualTo(ci.PublicKey));
        Assert.That(secrets.Machine.KeySource, Is.EqualTo(SecretEnvironment.KeyVariable));
        Assert.That(Resolve(null, "ci-only").Values["ci-only"], Is.EqualTo("pipeline"));
    }

    [Test]
    public void InvalidRepoInfraKeyIsAUsageError()
    {
        secrets.Variables[SecretEnvironment.KeyVariable] = "garbage";

        Assert.Throws<UsageException>(() => secrets.Machine.LoadKey());
    }

    [Test]
    public void ProblemsNameTheCommandAndVariableForEachSecret()
    {
        var resolution = new SecretResolution(new Dictionary<string, string>(), ["db/password"]) { Undecryptable = ["api/token"] };

        var withEnv = SecretProblems.Diagnostics(resolution, "prod");
        var withoutEnv = SecretProblems.Diagnostics(resolution, null);

        Assert.That(withEnv.Select(problem => problem.Code), Is.EqualTo(new[] { SecretCodes.Missing, SecretCodes.Undecryptable }));
        Assert.That(withEnv.All(problem => problem.Severity == Severity.Error), Is.True);
        Assert.That(withEnv[0].Message, Does.Contain("'infra secrets set prod/db/password'")
            .And.Contain("'infra secrets set db/password'")
            .And.Contain("INFRA_SECRET_PROD_DB_PASSWORD"));
        Assert.That(withoutEnv[0].Message, Does.Contain("'infra secrets set db/password'").And.Contain("INFRA_SECRET_DB_PASSWORD").And.Not.Contain("prod"));
        Assert.That(withEnv[1].Message, Does.Contain("api/token").And.Contain("cannot decrypt").And.Contain("infra secrets recipients add"));
    }

    [Test]
    public void ResolveAndRedactRegistersEveryValue()
    {
        Store(("db/password", "s3cr3t-value"));
        secrets.Variables["INFRA_SECRET_API_TOKEN"] = "env-token";
        var log = InfraLog.Silent();

        var resolution = new ChainedSecretResolver(secrets.Machine)
            .ResolveAndRedact(secrets.Repo.Workspace, null, [new SecretRef("db/password"), new SecretRef("api/token")], log);

        Assert.That(resolution.Values, Has.Count.EqualTo(2));
        Assert.That(log.Redact("pw=s3cr3t-value token=env-token"), Is.EqualTo("pw=(secret) token=(secret)"));
    }
}
