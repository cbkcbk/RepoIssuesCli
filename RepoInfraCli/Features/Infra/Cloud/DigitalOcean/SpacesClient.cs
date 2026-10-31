using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class SpacesClient(HttpClient http, string accessKey, string secretKey, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public static string Endpoint(string region) => $"https://{region}.digitaloceanspaces.com";

    public async Task<IReadOnlyList<string>> ListBucketsAsync(string region, CancellationToken cancellationToken)
    {
        var xml = await SendAsync(HttpMethod.Get, region, "/", "", null, null, cancellationToken) ?? "";
        return [.. Parse(xml).Descendants().Where(element => element.Name.LocalName == "Bucket").Select(bucket => Child(bucket, "Name")).OfType<string>()];
    }

    public Task<string?> GetAsync(string region, string bucket, string? key, string subresource, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, region, Path(bucket, key), subresource, null, null, cancellationToken, allowMissing: true);

    public Task PutAsync(string region, string bucket, string? key, string subresource, string? body, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, region, Path(bucket, key), subresource, body, headers, cancellationToken);

    public Task DeleteAsync(string region, string bucket, string? key, string subresource, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, region, Path(bucket, key), subresource, null, null, cancellationToken, allowMissing: true);

    static string Path(string bucket, string? key) =>
        "/" + Uri.EscapeDataString(bucket) + (key is null ? "" : "/" + string.Join('/', key.Split('/').Select(Uri.EscapeDataString)));

    public static XDocument Parse(string xml) => xml.Length == 0 ? new XDocument() : XDocument.Parse(xml);

    public static string? Child(XElement element, string name) => element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    async Task<string?> SendAsync(
        HttpMethod method, string region, string path, string subresource, string? body, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken,
        bool allowMissing = false)
    {
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var query = subresource.Length == 0 ? "" : subresource + "=";
        using var request = new HttpRequestMessage(method, new Uri($"{Endpoint(region)}{path}{(query.Length == 0 ? "" : "?" + query)}"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentMD5 = MD5.HashData(bytes);
        }
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
        SigV4.Sign(request, bytes, region, accessKey, secretKey, clock.GetUtcNow());

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var code = text.Length > 0 && text.TrimStart().StartsWith('<') ? Parse(text).Root is { } root ? Child(root, "Code") : null : null;
            throw new CloudApiException($"Spaces {method} {path}{(query.Length == 0 ? "" : "?" + subresource)} failed with {(int)response.StatusCode}: {code ?? response.ReasonPhrase}", (int)response.StatusCode);
        }
        return text;
    }
}

public static class SigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    public static void Sign(HttpRequestMessage request, byte[] body, string region, string accessKey, string secretKey, DateTimeOffset now)
    {
        var uri = request.RequestUri!;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        var payloadHash = Hex(SHA256.HashData(body));
        request.Headers.Host = uri.Host;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = uri.Host };
        foreach (var header in request.Headers.Where(header => header.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)))
            signed[header.Key.ToLowerInvariant()] = string.Join(',', header.Value).Trim();
        if (request.Content?.Headers.ContentMD5 is { } md5)
            signed["content-md5"] = Convert.ToBase64String(md5);

        var signedHeaders = string.Join(';', signed.Keys);
        var canonical = string.Join('\n',
            request.Method.Method,
            uri.AbsolutePath,
            CanonicalQuery(uri.Query),
            string.Concat(signed.Select(header => $"{header.Key}:{header.Value}\n")),
            signedHeaders,
            payloadHash);
        var scope = $"{date}/{region}/s3/aws4_request";
        var toSign = $"{Algorithm}\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, "s3");
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, toSign));
        request.Headers.TryAddWithoutValidation("Authorization", $"{Algorithm} Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    static string CanonicalQuery(string query) =>
        string.Join('&', query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Contains('=') ? pair : pair + "=")
            .Order(StringComparer.Ordinal));

    static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
