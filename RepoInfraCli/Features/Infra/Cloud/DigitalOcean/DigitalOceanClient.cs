using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class DigitalOceanClient(HttpClient http, string token) : RestClient(http, BaseUri, token, "DigitalOcean")
{
    public static readonly Uri BaseUri = new("https://api.digitalocean.com/");

    public async Task<IReadOnlyList<JsonElement>> ListAsync(string path, string property, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        for (string? next = path; next is not null;)
        {
            var page = await GetAsync(next, cancellationToken);
            items.AddRange(page.Items(property));
            next = page.Text("links", "pages", "next");
        }
        return items;
    }

    public async Task<JsonElement> PostAsync(string path, JsonNode body, string property, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Post, path, body, cancellationToken))?.At(property)
        ?? throw new CloudApiException($"DigitalOcean POST {path} returned no {property}");

    public Task DeleteAsync(string path, CancellationToken cancellationToken) => SendAsync(HttpMethod.Delete, path, null, cancellationToken);

    public async Task SyncTagsAsync(string resourceType, string id, IReadOnlyCollection<string> current, IReadOnlyCollection<string> desired, CancellationToken cancellationToken)
    {
        var resources = new JsonObject
        {
            ["resources"] = new JsonArray(new JsonObject { ["resource_id"] = id, ["resource_type"] = resourceType }),
        };
        foreach (var tag in desired.Except(current, StringComparer.Ordinal))
        {
            try
            {
                await SendAsync(HttpMethod.Post, "v2/tags", new JsonObject { ["name"] = tag }, cancellationToken);
            }
            catch (CloudApiException exception) when (exception.Status == 422)
            {
            }
            await SendAsync(HttpMethod.Post, $"v2/tags/{Uri.EscapeDataString(tag)}/resources", resources.DeepClone(), cancellationToken);
        }
        foreach (var tag in current.Except(desired, StringComparer.Ordinal))
            await SendAsync(HttpMethod.Delete, $"v2/tags/{Uri.EscapeDataString(tag)}/resources", resources.DeepClone(), cancellationToken);
    }

    public static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)JsonValue.Create(item))]);

    public static JsonArray Numbers(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)JsonValue.Create(long.Parse(item, System.Globalization.CultureInfo.InvariantCulture)))]);
}
