using System.Security.Cryptography;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Tests.Secrets;

public class SecretCipherTests
{
    [Test]
    public void RoundTripsForTheRecipient()
    {
        using var key = SecretKey.Generate();

        var encoded = SecretCipher.Encrypt("db/password", "hunter2 ✓", [key.PublicKey]);

        Assert.That(encoded, Does.StartWith("v1:"));
        Assert.That(encoded, Does.Not.Contain("hunter2"));
        Assert.That(SecretCipher.Decrypt("db/password", encoded, key), Is.EqualTo("hunter2 ✓"));
    }

    [Test]
    public void EncryptingTwiceGivesDifferentCiphertexts()
    {
        using var key = SecretKey.Generate();

        var first = SecretCipher.Encrypt("a", "same", [key.PublicKey]);
        var second = SecretCipher.Encrypt("a", "same", [key.PublicKey]);

        Assert.That(second, Is.Not.EqualTo(first));
    }

    [Test]
    public void EveryRecipientCanDecryptAndOthersCannot()
    {
        using var alice = SecretKey.Generate();
        using var bob = SecretKey.Generate();
        using var eve = SecretKey.Generate();

        var encoded = SecretCipher.Encrypt("api/token", "t0k3n", [alice.PublicKey, bob.PublicKey]);

        Assert.That(SecretCipher.Decrypt("api/token", encoded, alice), Is.EqualTo("t0k3n"));
        Assert.That(SecretCipher.Decrypt("api/token", encoded, bob), Is.EqualTo("t0k3n"));
        Assert.That(SecretCipher.Decrypt("api/token", encoded, eve), Is.Null);
        Assert.That(SecretCipher.KeyIds(encoded), Is.EquivalentTo(new[] { alice.KeyId, bob.KeyId }));
    }

    [Test]
    public void TamperedCiphertextFailsAuthentication()
    {
        using var key = SecretKey.Generate();
        var bytes = Convert.FromBase64String(SecretCipher.Encrypt("a", "value", [key.PublicKey])["v1:".Length..]);
        bytes[^1] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => SecretCipher.Decrypt("a", "v1:" + Convert.ToBase64String(bytes), key));
    }

    [Test]
    public void TamperedWrappedKeyFailsAuthentication()
    {
        using var key = SecretKey.Generate();
        var bytes = Convert.FromBase64String(SecretCipher.Encrypt("a", "value", [key.PublicKey])["v1:".Length..]);
        bytes[12 + 16 + 1 + 8 + 65 + 12] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => SecretCipher.Decrypt("a", "v1:" + Convert.ToBase64String(bytes), key));
    }

    [Test]
    public void ValueMovedToAnotherNameFailsAuthentication()
    {
        using var key = SecretKey.Generate();
        var encoded = SecretCipher.Encrypt("staging/db", "staging-pass", [key.PublicKey]);

        Assert.That(() => SecretCipher.Decrypt("prod/db", encoded, key), Throws.InstanceOf<CryptographicException>());
    }

    [Test]
    public void MalformedValuesAreCryptographicErrors()
    {
        using var key = SecretKey.Generate();

        Assert.That(() => SecretCipher.Decrypt("a", "v2:AAAA", key), Throws.InstanceOf<CryptographicException>());
        Assert.That(() => SecretCipher.Decrypt("a", "v1:not base64!", key), Throws.InstanceOf<CryptographicException>());
        Assert.That(() => SecretCipher.Decrypt("a", "v1:AAAA", key), Throws.InstanceOf<CryptographicException>());
    }

    [Test]
    public void ShareAddsARecipientWithoutChangingTheValue()
    {
        using var alice = SecretKey.Generate();
        using var bob = SecretKey.Generate();
        var encoded = SecretCipher.Encrypt("a", "value", [alice.PublicKey]);

        var shared = SecretCipher.Share("a", encoded, alice, bob.PublicKey)!;

        Assert.That(SecretCipher.Decrypt("a", shared, bob), Is.EqualTo("value"));
        Assert.That(SecretCipher.Share("a", encoded, bob, alice.PublicKey), Is.EqualTo(encoded), "already a recipient");
        Assert.That(SecretCipher.Share("a", shared, SecretKey.Generate(), SecretKey.Generate().PublicKey), Is.Null, "sharer cannot decrypt");
    }

    [Test]
    public void KeysRoundTripThroughTheirTextForm()
    {
        using var key = SecretKey.Generate();
        var text = key.Export();

        using var parsed = SecretKey.Parse(text + "\n");

        Assert.That(text, Does.StartWith(SecretKey.PrivatePrefix));
        Assert.That(key.PublicKey, Does.StartWith(RecipientKey.Prefix));
        Assert.That(parsed.PublicKey, Is.EqualTo(key.PublicKey));
        Assert.That(RecipientKey.Id(key.PublicKey), Is.EqualTo(key.KeyId));
    }

    [Test]
    public void InvalidKeysAreRejected()
    {
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<FormatException>(() => SecretKey.Parse("nope"));
        Assert.Throws<FormatException>(() => SecretKey.Parse(SecretKey.PrivatePrefix + "AAAA"));
        Assert.Throws<FormatException>(() => SecretKey.Parse(SecretKey.PrivatePrefix + Convert.ToBase64String(p384.ExportPkcs8PrivateKey())));
        Assert.Throws<FormatException>(() => RecipientKey.Decode("p256:AAAA"));
        Assert.Throws<FormatException>(() => RecipientKey.Decode(RecipientKey.Format(p384.ExportSubjectPublicKeyInfo())));
    }
}
