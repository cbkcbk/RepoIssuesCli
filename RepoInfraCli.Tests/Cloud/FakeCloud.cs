using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoInfraCli.Tests.Cloud;

public sealed record SentRequest(string Method, string Url, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
}

public sealed class FakeCloud : HttpMessageHandler
{
    sealed record Route(string Method, Regex Url, Queue<(HttpStatusCode Status, string Body)> Responses);

    readonly List<Route> routes = [];

    public ConcurrentQueue<SentRequest> Requests { get; } = new();

    public static string FixtureFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Cloud", "Fixtures");

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureFolder, name));

    public FakeCloud On(string method, string url, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        OnPattern(method, "^" + Regex.Escape(url) + "$", body, status);

    public FakeCloud Set(string method, string url, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        routes.RemoveAll(route => route.Method == method && route.Url.ToString() == "^" + Regex.Escape(url) + "$");
        return On(method, url, body, status);
    }

    public FakeCloud OnPattern(string method, string pattern, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var route = routes.FirstOrDefault(existing => existing.Method == method && existing.Url.ToString() == pattern);
        if (route is null)
        {
            route = new Route(method, new Regex(pattern), new Queue<(HttpStatusCode, string)>());
            routes.Add(route);
        }
        route.Responses.Enqueue((status, body));
        return this;
    }

    public IReadOnlyList<SentRequest> Sent(string method, string urlPart) =>
        [.. Requests.Where(request => request.Method == method && request.Url.Contains(urlPart, StringComparison.Ordinal))];

    public IReadOnlyList<SentRequest> Changes => [.. Requests.Where(request => request.Method != "GET")];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new SentRequest(request.Method.Method, url, body, headers));

        var route = routes.LastOrDefault(candidate => candidate.Method == request.Method.Method && candidate.Url.IsMatch(url));
        if (route is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($$"""{"id":"not_found","message":"no fixture for {{request.Method}} {{url}}"}""", Encoding.UTF8, "application/json"),
            };

        var (status, text) = route.Responses.Count > 1 ? route.Responses.Dequeue() : route.Responses.Peek();
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, text.StartsWith('<') ? "application/xml" : "application/json") };
    }
}
