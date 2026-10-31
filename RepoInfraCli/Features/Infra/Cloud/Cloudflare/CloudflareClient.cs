using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoInfraCli.Features.Infra.Cloud.Cloudflare;

public sealed class CloudflareClient(HttpClient http, string token) : RestClient(http, BaseUri, token, "Cloudflare")
{
    public static readonly Uri BaseUri = new("https://api.cloudflare.com/");

    public async Task<IReadOnlyList<JsonElement>> ListAsync(string path, int pageSize, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; ; page++)
        {
            var response = await GetAsync($"{path}{separator}per_page={pageSize}&page={page}", cancellationToken);
            items.AddRange(response.Items("result"));
            if (page >= (int.TryParse(response.Text("result_info", "total_pages"), out var pages) ? pages : 1))
                return items;
        }
    }

    public async Task<JsonElement> ResultAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken) =>
        (await SendAsync(method, path, body, cancellationToken))?.At("result")
        ?? throw new CloudApiException($"Cloudflare {method} {path} returned no result");
}
