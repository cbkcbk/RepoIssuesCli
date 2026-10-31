using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoInfraCli.Features.Infra.Cloud;

public abstract class RestClient(HttpClient http, Uri baseUri, string token, string service)
{
    public async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, path, null, cancellationToken) ?? throw new CloudApiException($"{service} GET {path} returned no body");

    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CloudApiException($"{service} {method} {request.RequestUri!.AbsolutePath} failed with {(int)response.StatusCode}: {ErrorMessage(text)}", (int)response.StatusCode);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    static string ErrorMessage(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.Text("message")
                   ?? root.Items("errors").Select(error => error.Text("message")).FirstOrDefault(message => message is not null)
                   ?? Shorten(text);
        }
        catch (JsonException)
        {
            return Shorten(text);
        }
    }

    static string Shorten(string text) => text.Length > 200 ? text[..200] + "..." : text;
}
