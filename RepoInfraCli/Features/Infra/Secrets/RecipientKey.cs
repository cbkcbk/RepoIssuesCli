using System.Security.Cryptography;

namespace RepoInfraCli.Features.Infra.Secrets;

public static class RecipientKey
{
    public const string Prefix = "p256:";
    public const int IdLength = 8;

    public static string Format(byte[] subjectPublicKeyInfo) => Prefix + Convert.ToBase64String(subjectPublicKeyInfo);

    public static byte[] Decode(string publicKey)
    {
        var trimmed = publicKey.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException($"A recipient public key starts with '{Prefix}'.");

        try
        {
            var bytes = Convert.FromBase64String(trimmed[Prefix.Length..]);
            using var key = Import(bytes);
            if (key.KeySize != 256)
                throw new FormatException("A recipient public key must be a P-256 key.");
            return bytes;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new FormatException($"'{trimmed}' is not a valid P-256 public key.", exception);
        }
    }

    public static string Normalize(string publicKey) => Format(Decode(publicKey));

    public static ECDiffieHellman Import(byte[] subjectPublicKeyInfo)
    {
        var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return key;
    }

    public static string Id(string publicKey) => Convert.ToHexStringLower(IdBytes(Decode(publicKey)));

    public static byte[] IdBytes(byte[] subjectPublicKeyInfo) => SHA256.HashData(subjectPublicKeyInfo)[..IdLength];
}
