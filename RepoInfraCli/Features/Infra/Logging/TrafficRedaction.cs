using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace RepoInfraCli.Features.Infra.Logging;

public static class TrafficRedaction
{
    public const int MaxBodyLength = 64 * 1024;

    static readonly FrozenSet<string> CredentialHeaders = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Amz-Security-Token", "X-Auth-Token", "X-Auth-Key", "X-Api-Key", "X-Registry-Auth");

    static readonly FrozenSet<string> CredentialQueryParameters = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "X-Amz-Signature", "X-Amz-Credential", "X-Amz-Security-Token", "access_token");

    static readonly FrozenSet<string> CredentialProperties = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "password", "secret", "token", "access_token", "refresh_token", "id_token", "identitytoken", "private_key", "api_key", "api_token",
        "access_key", "secret_key", "secret_access_key", "uri", "private_uri", "connection_string", "auth");

    static readonly byte[] SecretJson = Encoding.UTF8.GetBytes($"\"{SecretRedactor.Replacement}\"");

    public static string Headers(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
        string.Join("\n", headers.Select(header =>
            $"{header.Key}: {(CredentialHeaders.Contains(header.Key) ? SecretRedactor.Replacement : string.Join(", ", header.Value))}"));

    public static string Url(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
            return uri.ToString();

        var parameters = uri.Query.TrimStart('?').Split('&').Select(parameter =>
            parameter.Split('=', 2) is [var name, _] && CredentialQueryParameters.Contains(Uri.UnescapeDataString(name))
                ? $"{name}={SecretRedactor.Replacement}"
                : parameter);
        return $"{uri.GetLeftPart(UriPartial.Path)}?{string.Join('&', parameters)}";
    }

    public static bool IsText(string? mediaType) =>
        mediaType is not null
        && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("yaml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));

    public static string Binary(string? mediaType, long? length) =>
        $"({mediaType ?? "binary"}, {(length is { } bytes ? $"{bytes} bytes" : "unknown length")})";

    public static string Body(string text, ISecretRedactor secrets)
    {
        var redacted = secrets.Redact(Json(text));
        return redacted.Length <= MaxBodyLength
            ? redacted
            : $"{redacted[..MaxBodyLength]}... (truncated, {Encoding.UTF8.GetByteCount(redacted)} bytes)";
    }

    public static string Bytes(ReadOnlySpan<byte> bytes, ISecretRedactor secrets)
    {
        try
        {
            return Body(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), secrets);
        }
        catch (DecoderFallbackException)
        {
            return Binary(null, bytes.Length);
        }
    }

    public static string Json(string text)
    {
        var start = text.AsSpan().TrimStart();
        if (start.IsEmpty || start[0] is not ('{' or '['))
            return text;

        var bytes = Encoding.UTF8.GetBytes(text);
        var secrets = CredentialValues(bytes);
        if (secrets.Count == 0)
            return text;

        var output = new List<byte>(bytes.Length);
        var position = 0;
        foreach (var (offset, length) in secrets)
        {
            output.AddRange(bytes.AsSpan(position, offset - position));
            output.AddRange(SecretJson);
            position = offset + length;
        }

        output.AddRange(bytes.AsSpan(position));
        return Encoding.UTF8.GetString([.. output]);
    }

    static List<(int Offset, int Length)> CredentialValues(byte[] json)
    {
        var found = new List<(int, int)>();
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !CredentialProperties.Contains(reader.GetString()!))
                    continue;
                if (!reader.Read())
                    break;
                if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
                    found.Add(((int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex)));
            }
        }
        catch (JsonException)
        {
        }

        return found;
    }
}