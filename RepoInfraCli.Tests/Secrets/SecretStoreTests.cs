using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Tests.Secrets;

public class SecretStoreTests
{
    [Test]
    public void SerializesOneSortedLinePerEntryAndParsesBack()
    {
        using var key = SecretKey.Generate();
        var store = new SecretStore();
        store.AddRecipient("cbk <cbk@snappy.no>", key.PublicKey, key);
        store.AddRecipient("ci", SecretKey.Generate().PublicKey, key);
        store.Set("zeta/token", "z");
        store.Set("alpha/password", "a");

        var text = store.Serialize();
        var lines = text.Split('\n');
        var parsed = SecretStore.Parse(text);

        Assert.That(lines[1], Is.EqualTo("recipients:"));
        Assert.That(lines[2], Does.StartWith("  \"cbk <cbk@snappy.no>\": p256:"));
        Assert.That(lines[3], Does.StartWith("  ci: p256:"));
        Assert.That(lines[4], Is.EqualTo("secrets:"));
        Assert.That(lines[5], Does.StartWith("  alpha/password: v1:"));
        Assert.That(lines[6], Does.StartWith("  zeta/token: v1:"));
        Assert.That(parsed.Recipients, Is.EqualTo(store.Recipients));
        Assert.That(parsed.Secrets, Is.EqualTo(store.Secrets));
        Assert.That(parsed.Serialize(), Is.EqualTo(text));
    }

    [Test]
    public void EmptyStoreIsValidYaml()
    {
        var text = new SecretStore().Serialize();

        Assert.That(text, Does.Contain("recipients: {}\nsecrets: {}\n"));
        Assert.That(SecretStore.Parse(text).Secrets, Is.Empty);
    }

    [Test]
    public void YamlKeywordsAndOddNamesAreQuoted()
    {
        var store = new SecretStore();
        store.Recipients["null"] = "p256:x";
        store.Recipients["has \"quotes\" \\ and: colon"] = "p256:y";

        var parsed = SecretStore.Parse(store.Serialize());

        Assert.That(parsed.Recipients.Keys, Is.EquivalentTo(store.Recipients.Keys));
    }

    [Test]
    public void UnknownSectionsAndBadYamlAreUsageErrors()
    {
        Assert.Throws<UsageException>(() => SecretStore.Parse("values: {}\n"));
        Assert.Throws<UsageException>(() => SecretStore.Parse("secrets: [\n"));
    }

    [TestCase("db/password", true)]
    [TestCase("prod/cloudflare/dns_token", true)]
    [TestCase("a.b-c", true)]
    [TestCase("/db", false)]
    [TestCase("db/", false)]
    [TestCase("db//x", false)]
    [TestCase("db password", false)]
    [TestCase("", false)]
    public void ValidatesNames(string name, bool valid) => Assert.That(SecretStore.IsValidName(name), Is.EqualTo(valid));

    [Test]
    public void SetWithoutRecipientsIsAUsageError() =>
        Assert.Throws<UsageException>(() => new SecretStore().Set("a", "b"));

    [Test]
    public void RemovedRecipientCannotDecryptAndIsToldWhatToRotate()
    {
        using var alice = SecretKey.Generate();
        using var bob = SecretKey.Generate();
        var store = new SecretStore();
        store.AddRecipient("alice", alice.PublicKey, alice);
        store.Set("only/alice", "1");
        store.AddRecipient("bob", bob.PublicKey, alice);
        store.Set("both", "2");

        var change = store.RemoveRecipient("bob", alice);

        Assert.That(change.Secrets, Is.EqualTo(new[] { "both", "only/alice" }), "bob was shared only/alice when added");
        Assert.That(change.Skipped, Is.Empty);
        Assert.That(store.Recipients.Keys, Is.EqualTo(new[] { "alice" }));
        Assert.That(store.Reveal("both", bob).Status, Is.EqualTo(SecretRevealStatus.NotRecipient));
        Assert.That(store.Reveal("only/alice", bob).Status, Is.EqualTo(SecretRevealStatus.NotRecipient));
        Assert.That(store.Reveal("both", alice).Value, Is.EqualTo("2"));
    }

    [Test]
    public void RemovingWithoutAKeyStillDropsTheWrap()
    {
        using var alice = SecretKey.Generate();
        using var bob = SecretKey.Generate();
        var store = new SecretStore();
        store.AddRecipient("alice", alice.PublicKey, alice);
        store.AddRecipient("bob", bob.PublicKey, alice);
        store.Set("x", "1");

        var change = store.RemoveRecipient("bob", null);

        Assert.That(change.Secrets, Is.EqualTo(new[] { "x" }));
        Assert.That(change.Skipped, Is.EqualTo(new[] { "x" }));
        Assert.That(store.Reveal("x", bob).Status, Is.EqualTo(SecretRevealStatus.NotRecipient));
        Assert.That(store.Reveal("x", alice).Value, Is.EqualTo("1"));
    }

    [Test]
    public void RemovingTheLastOrAnUnknownRecipientIsRefused()
    {
        using var alice = SecretKey.Generate();
        var store = new SecretStore();
        store.AddRecipient("alice", alice.PublicKey, alice);

        Assert.Throws<UsageException>(() => store.RemoveRecipient("alice", alice));
        Assert.Throws<SecretNotFoundException>(() => store.RemoveRecipient("nobody", alice));
        Assert.That(store.Recipients.Keys, Is.EqualTo(new[] { "alice" }));
    }

    [Test]
    public void AddingRecipientSkipsSecretsTheSharerCannotRead()
    {
        using var alice = SecretKey.Generate();
        using var bob = SecretKey.Generate();
        using var carol = SecretKey.Generate();
        var store = new SecretStore();
        store.AddRecipient("alice", alice.PublicKey, alice);
        store.Set("a", "1");
        store.AddRecipient("bob", bob.PublicKey, null);
        store.Set("b", "2");

        var change = store.AddRecipient("carol", carol.PublicKey, bob);

        Assert.That(change.Secrets, Is.EqualTo(new[] { "b" }));
        Assert.That(change.Skipped, Is.EqualTo(new[] { "a" }));
        Assert.That(store.Readers("b"), Is.EqualTo(new[] { "alice", "bob", "carol" }));
        Assert.That(store.Readers("a"), Is.EqualTo(new[] { "alice" }));
    }

    [Test]
    public void SameNameWithAnotherKeyOrSameKeyWithAnotherNameIsRefused()
    {
        using var alice = SecretKey.Generate();
        using var bob = SecretKey.Generate();
        var store = new SecretStore();
        store.AddRecipient("alice", alice.PublicKey, alice);

        Assert.Throws<UsageException>(() => store.AddRecipient("alice", bob.PublicKey, alice));
        Assert.Throws<UsageException>(() => store.AddRecipient("alias", alice.PublicKey, alice));
    }
}
