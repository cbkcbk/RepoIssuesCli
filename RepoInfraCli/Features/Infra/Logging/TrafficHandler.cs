namespace RepoInfraCli.Features.Infra.Logging;

public sealed class TrafficHandler(HttpMessageHandler inner, string? provider = null) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (InfraLog.Current is not { RecordsTraffic: true } log)
            return await base.SendAsync(request, cancellationToken);

        var url = TrafficRedaction.Url(request.RequestUri!);
        var call = log.Request(provider ?? ProviderOf(request.RequestUri!), $"{request.Method} {url}", new Dictionary<string, object?>
        {
            ["Method"] = request.Method.Method,
            ["Url"] = url,
            ["Headers"] = TrafficRedaction.Headers(request.Content is null ? request.Headers : [.. request.Headers, .. request.Content.Headers]),
            ["Body"] = await BodyAsync(request.Content, log, cancellationToken),
        });

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            call.Response($"{(int)response.StatusCode} {response.ReasonPhrase}", new Dictionary<string, object?>
            {
                ["Status"] = (int)response.StatusCode,
                ["Headers"] = TrafficRedaction.Headers([.. response.Headers, .. response.Content.Headers]),
                ["Body"] = await BodyAsync(response.Content, log, cancellationToken),
            });
            return response;
        }
        catch (Exception exception)
        {
            call.Failed(exception);
            throw;
        }
    }

    public static string ProviderOf(Uri uri) => uri.Host switch
    {
        "api.digitalocean.com" => "digitalocean",
        "api.cloudflare.com" => "cloudflare",
        var host when host.EndsWith(".digitaloceanspaces.com", StringComparison.OrdinalIgnoreCase) => "spaces",
        var host => host,
    };

    static async Task<string?> BodyAsync(HttpContent? content, InfraLog log, CancellationToken cancellationToken)
    {
        if (content is null || content.Headers.ContentLength == 0)
            return null;

        var mediaType = content.Headers.ContentType?.MediaType;
        if (!TrafficRedaction.IsText(mediaType))
            return TrafficRedaction.Binary(mediaType, content.Headers.ContentLength);

        await content.LoadIntoBufferAsync(cancellationToken);
        return TrafficRedaction.Body(await content.ReadAsStringAsync(cancellationToken), log);
    }
}