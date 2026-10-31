using System.Security.Cryptography;
using System.Text;

namespace RepoInfraCli.Features.Infra.Secrets;

// "v1:" + base64 of: nonce[12] tag[16] count[1] count × (keyId[8] ephemeral[65] wrapNonce[12] wrappedKey[32] wrapTag[16]) ciphertext[..]
// The value is AES-256-GCM under a random data key; each recipient gets the data key wrapped with AES-256-GCM under
// HKDF-SHA256(ECDH(ephemeral, recipient), salt = ephemeral point + recipient SPKI). Both use "repo-infra:v1:<name>" as associated data.
public static class SecretCipher
{
    public const string Prefix = "v1:";

    const int NonceSize = 12;
    const int TagSize = 16;
    const int DataKeySize = 32;
    const int PointSize = 65;
    const int WrapSize = RecipientKey.IdLength + PointSize + NonceSize + DataKeySize + TagSize;
    static readonly byte[] WrapInfo = "repo-infra:v1:wrap"u8.ToArray();

    sealed record Wrap(byte[] KeyId, byte[] Ephemeral, byte[] Nonce, byte[] Key, byte[] Tag)
    {
        public string Id => Convert.ToHexStringLower(KeyId);
    }

    sealed record Envelope(byte[] Nonce, byte[] Tag, IReadOnlyList<Wrap> Wraps, byte[] Ciphertext);

    public static string Encrypt(string name, string value, IEnumerable<string> recipients)
    {
        var dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        try
        {
            var plaintext = Encoding.UTF8.GetBytes(value);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(dataKey, TagSize))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(name));

            var wraps = recipients.Select(RecipientKey.Decode).DistinctBy(Convert.ToBase64String).Select(spki => WrapFor(name, dataKey, spki)).ToList();
            if (wraps.Count == 0)
                throw new InvalidOperationException("A secret needs at least one recipient.");
            return Encode(new Envelope(nonce, tag, wraps, ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static string? Decrypt(string name, string encoded, SecretKey key)
    {
        var envelope = Decode(encoded);
        if (DataKey(name, envelope, key) is not { } dataKey)
            return null;

        try
        {
            var plaintext = new byte[envelope.Ciphertext.Length];
            using (var aes = new AesGcm(dataKey, TagSize))
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, Aad(name));
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static IReadOnlyList<string> KeyIds(string encoded) => [.. Decode(encoded).Wraps.Select(wrap => wrap.Id)];

    public static bool IsReadableBy(string encoded, string keyId) => KeyIds(encoded).Contains(keyId);

    public static string? Share(string name, string encoded, SecretKey key, string recipient)
    {
        var envelope = Decode(encoded);
        var spki = RecipientKey.Decode(recipient);
        var id = Convert.ToHexStringLower(RecipientKey.IdBytes(spki));
        if (envelope.Wraps.Any(wrap => wrap.Id == id))
            return encoded;
        if (DataKey(name, envelope, key) is not { } dataKey)
            return null;

        try
        {
            return Encode(envelope with { Wraps = [.. envelope.Wraps, WrapFor(name, dataKey, spki)] });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public static string Unshare(string encoded, string keyId)
    {
        var envelope = Decode(encoded);
        return Encode(envelope with { Wraps = [.. envelope.Wraps.Where(wrap => wrap.Id != keyId)] });
    }

    static byte[] Aad(string name) => Encoding.UTF8.GetBytes($"repo-infra:v1:{name}");

    static Wrap WrapFor(string name, byte[] dataKey, byte[] recipientInfo)
    {
        using var recipient = RecipientKey.Import(recipientInfo);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var point = Point(ephemeral.ExportParameters(false).Q);
        var wrapKey = WrapKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), point, recipientInfo);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var wrapped = new byte[DataKeySize];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(wrapKey, TagSize))
            aes.Encrypt(nonce, dataKey, wrapped, tag, Aad(name));
        CryptographicOperations.ZeroMemory(wrapKey);
        return new Wrap(RecipientKey.IdBytes(recipientInfo), point, nonce, wrapped, tag);
    }

    static byte[]? DataKey(string name, Envelope envelope, SecretKey key)
    {
        if (envelope.Wraps.FirstOrDefault(wrap => wrap.Id == key.KeyId) is not { } wrap)
            return null;

        using var ephemeral = FromPoint(wrap.Ephemeral);
        var wrapKey = WrapKey(key.Agree(ephemeral.PublicKey), wrap.Ephemeral, key.PublicKeyInfo);
        var dataKey = new byte[DataKeySize];
        using (var aes = new AesGcm(wrapKey, TagSize))
            aes.Decrypt(wrap.Nonce, wrap.Key, wrap.Tag, dataKey, Aad(name));
        CryptographicOperations.ZeroMemory(wrapKey);
        return dataKey;
    }

    static byte[] WrapKey(byte[] sharedSecret, byte[] ephemeralPoint, byte[] recipientInfo)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, DataKeySize, [.. ephemeralPoint, .. recipientInfo], WrapInfo);
        CryptographicOperations.ZeroMemory(sharedSecret);
        return key;
    }

    static byte[] Point(ECPoint q) => [0x04, .. q.X!, .. q.Y!];

    static ECDiffieHellman FromPoint(byte[] point)
    {
        if (point.Length != PointSize || point[0] != 0x04)
            throw new CryptographicException("Malformed ephemeral key.");
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..] },
        });
    }

    static string Encode(Envelope envelope)
    {
        using var buffer = new MemoryStream();
        buffer.Write(envelope.Nonce);
        buffer.Write(envelope.Tag);
        buffer.WriteByte(checked((byte)envelope.Wraps.Count));
        foreach (var wrap in envelope.Wraps)
        {
            buffer.Write(wrap.KeyId);
            buffer.Write(wrap.Ephemeral);
            buffer.Write(wrap.Nonce);
            buffer.Write(wrap.Key);
            buffer.Write(wrap.Tag);
        }
        buffer.Write(envelope.Ciphertext);
        return Prefix + Convert.ToBase64String(buffer.ToArray());
    }

    static Envelope Decode(string encoded)
    {
        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException($"Unsupported secret encoding; expected '{Prefix}'.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded[Prefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("Malformed secret value.", exception);
        }

        var header = NonceSize + TagSize + 1;
        if (bytes.Length < header || bytes.Length < header + bytes[header - 1] * WrapSize)
            throw new CryptographicException("Malformed secret value.");

        var span = bytes.AsSpan();
        var wraps = new List<Wrap>();
        var offset = header;
        for (var index = 0; index < bytes[header - 1]; index++, offset += WrapSize)
        {
            var wrap = span.Slice(offset, WrapSize);
            wraps.Add(new Wrap(
                wrap[..RecipientKey.IdLength].ToArray(),
                wrap.Slice(RecipientKey.IdLength, PointSize).ToArray(),
                wrap.Slice(RecipientKey.IdLength + PointSize, NonceSize).ToArray(),
                wrap.Slice(RecipientKey.IdLength + PointSize + NonceSize, DataKeySize).ToArray(),
                wrap[^TagSize..].ToArray()));
        }

        return new Envelope(span[..NonceSize].ToArray(), span.Slice(NonceSize, TagSize).ToArray(), wraps, span[offset..].ToArray());
    }
}
