using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed record RegistryCredentials(string Username, string Password);

public sealed partial class RegistryClient(IProcessRunner runner, HttpMessageHandler? handler = null, string? dockerConfigFile = null)
{
    static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    public async Task<TagCheck> TagExistsAsync(string registry, string repository, string tag, CancellationToken cancellationToken)
    {
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(30);
            var url = $"{Scheme(registry)}://{registry}/v2/{repository}/manifests/{Uri.EscapeDataString(tag)}";

            using var first = await HeadAsync(client, url, null, cancellationToken);
            if (first.StatusCode != HttpStatusCode.Unauthorized)
                return Answer(first);

            var challenge = first.Headers.WwwAuthenticate.FirstOrDefault();
            var credentials = await CredentialsAsync(registry, cancellationToken);
            var authorization = challenge?.Scheme switch
            {
                "Bearer" => await TokenAsync(client, challenge.Parameter ?? "", repository, credentials, cancellationToken),
                "Basic" when credentials is not null => Basic(credentials),
                _ => null,
            };
            if (authorization is null)
                return new TagCheck(null, $"{registry} needs credentials; log in with docker login {registry}");

            using var second = await HeadAsync(client, url, authorization, cancellationToken);
            return Answer(second);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or FormatException or IOException)
        {
            return new TagCheck(null, exception.Message);
        }
    }

    public static string Scheme(string registry)
    {
        var host = registry.StartsWith('[') ? registry[..(registry.IndexOf(']') + 1)] : registry.Split(':')[0];
        return host is "localhost" or "127.0.0.1" or "[::1]" ? "http" : "https";
    }

    static TagCheck Answer(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.OK => new TagCheck(true),
        HttpStatusCode.NotFound => new TagCheck(false),
        _ => new TagCheck(null, $"registry answered {(int)response.StatusCode} {response.ReasonPhrase}"),
    };

    static async Task<HttpResponseMessage> HeadAsync(HttpClient client, string url, AuthenticationHeaderValue? authorization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        request.Headers.Authorization = authorization;
        return await client.SendAsync(request, cancellationToken);
    }

    static async Task<AuthenticationHeaderValue?> TokenAsync(HttpClient client, string parameter, string repository, RegistryCredentials? credentials, CancellationToken cancellationToken)
    {
        var fields = ChallengeField().Matches(parameter).ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("realm", out var realm))
            return null;

        var query = new List<string>();
        if (fields.TryGetValue("service", out var service))
            query.Add($"service={Uri.EscapeDataString(service)}");
        query.Add($"scope={Uri.EscapeDataString(fields.GetValueOrDefault("scope") ?? $"repository:{repository}:pull")}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{realm}{(realm.Contains('?') ? '&' : '?')}{string.Join('&', query)}");
        if (credentials is not null)
            request.Headers.Authorization = Basic(credentials);

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var token = root.TryGetProperty("token", out var value) || root.TryGetProperty("access_token", out value) ? value.GetString() : null;
        return token is null ? null : new AuthenticationHeaderValue("Bearer", token);
    }

    static AuthenticationHeaderValue Basic(RegistryCredentials credentials) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}")));

    async Task<RegistryCredentials?> CredentialsAsync(string registry, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_CONTAINER_REGISTRY_UNAME") is { Length: > 0 } user
            && Environment.GetEnvironmentVariable("DOTNET_CONTAINER_REGISTRY_PWD") is { Length: > 0 } password)
            return new RegistryCredentials(user, password);

        var path = dockerConfigFile ?? Path.Combine(
            Environment.GetEnvironmentVariable("DOCKER_CONFIG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker"),
            "config.json");
        if (!File.Exists(path))
            return null;

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        var root = document.RootElement;
        if (root.TryGetProperty("auths", out var auths) && auths.ValueKind == JsonValueKind.Object
            && auths.EnumerateObject().FirstOrDefault(entry => HostOf(entry.Name) == registry).Value is { ValueKind: JsonValueKind.Object } entry
            && entry.TryGetProperty("auth", out var auth) && auth.GetString() is { Length: > 0 } encoded
            && Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split(':', 2) is [var name, var secret])
            return new RegistryCredentials(name, secret);

        var helper = root.TryGetProperty("credHelpers", out var helpers) && helpers.ValueKind == JsonValueKind.Object && helpers.TryGetProperty(registry, out var specific)
            ? specific.GetString()
            : root.TryGetProperty("credsStore", out var store) ? store.GetString() : null;
        return helper is { Length: > 0 } ? await HelperAsync(helper, registry, cancellationToken) : null;
    }

    async Task<RegistryCredentials?> HelperAsync(string helper, string registry, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync($"docker-credential-{helper}", ["get"], null, registry, cancellationToken);
            if (!result.Succeeded)
                return null;
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            return root.GetProperty("Username").GetString() is { } name && root.GetProperty("Secret").GetString() is { } secret ? new RegistryCredentials(name, secret) : null;
        }
        catch (Exception exception) when (exception is Win32Exception or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    static string HostOf(string entry)
    {
        var withoutScheme = entry.Contains("://", StringComparison.Ordinal) ? entry[(entry.IndexOf("://", StringComparison.Ordinal) + 3)..] : entry;
        return withoutScheme.Split('/')[0];
    }

    [GeneratedRegex("(?<key>[A-Za-z]+)=\"(?<value>[^\"]*)\"")]
    private static partial Regex ChallengeField();
}
