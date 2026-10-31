using System.Security.Cryptography;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed class SecretKey : IDisposable
{
    public const string PrivatePrefix = "p256-key:";

    readonly ECDiffieHellman key;

    SecretKey(ECDiffieHellman key)
    {
        this.key = key;
        PublicKeyInfo = key.ExportSubjectPublicKeyInfo();
        PublicKey = RecipientKey.Format(PublicKeyInfo);
        KeyId = Convert.ToHexStringLower(RecipientKey.IdBytes(PublicKeyInfo));
    }

    public byte[] PublicKeyInfo { get; }

    public string PublicKey { get; }

    public string KeyId { get; }

    public static SecretKey Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static SecretKey Parse(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(PrivatePrefix, StringComparison.Ordinal))
            throw new FormatException($"A repo-infra key starts with '{PrivatePrefix}'.");

        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(trimmed[PrivatePrefix.Length..]), out _);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            key.Dispose();
            throw new FormatException("The repo-infra key is not a valid P-256 private key.", exception);
        }

        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new FormatException("The repo-infra key must be a P-256 key.");
        }
        return new SecretKey(key);
    }

    public string Export() => PrivatePrefix + Convert.ToBase64String(key.ExportPkcs8PrivateKey());

    public byte[] Agree(ECDiffieHellmanPublicKey other) => key.DeriveRawSecretAgreement(other);

    public void Dispose() => key.Dispose();
}
