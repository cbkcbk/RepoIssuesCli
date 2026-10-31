using System.Text.Json;
using RepoInfraCli.Features.Infra.Cloud.Cloudflare;
using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed class ProviderSession(ProviderNode provider, CloudServices cloud, SecretLookup secrets, InfraLog log)
{
    public const string SpacesAccessKey = "spaces_access_key";
    public const string SpacesSecretKey = "spaces_secret_key";

    DigitalOceanClient? digitalOcean;
    CloudflareClient? cloudflare;
    SpacesClient? spaces;

    public ProviderNode Provider => provider;

    public InfraLog Log => log;

    public HashSet<string> SpacesRegions { get; } = new(StringComparer.Ordinal);

    public DigitalOceanClient DigitalOcean => digitalOcean ??= new DigitalOceanClient(cloud.Http, Token());

    public CloudflareClient Cloudflare => cloudflare ??= new CloudflareClient(cloud.Http, Token());

    public SpacesClient Spaces => spaces ??= new SpacesClient(cloud.Http, secrets.Get(SpacesSecret(SpacesAccessKey)), secrets.Get(SpacesSecret(SpacesSecretKey)));

    public string Token() => secrets.Get(TokenSecret);

    SecretRef TokenSecret =>
        provider.Options[provider.Kind == CloudflareModule.ProviderKind ? "api_token" : "token"] as SecretRef
        ?? throw new ResourcePlanException($"{provider.Kind}.provider has no token secret");

    public SecretRef SpacesSecret(string name)
    {
        var path = TokenSecret.Path;
        var slash = path.LastIndexOf('/');
        return new SecretRef(slash < 0 ? name : path[..(slash + 1)] + name);
    }

    public string Sensitive(string value)
    {
        log.AddSecret(value);
        return value;
    }

    public async Task<JsonElement> WaitAsync(string what, Func<CancellationToken, Task<JsonElement?>> attempt, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + cloud.WaitTimeout;
        while (true)
        {
            if (await attempt(cancellationToken) is { } done)
                return done;
            if (DateTimeOffset.UtcNow > deadline)
                throw new CloudApiException($"timed out after {cloud.WaitTimeout} waiting for {what}");
            log.Debug("cloud.wait", $"waiting for {what}");
            await Task.Delay(cloud.PollInterval, cancellationToken);
        }
    }
}
