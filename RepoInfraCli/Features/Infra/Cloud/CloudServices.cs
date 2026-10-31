using RepoInfraCli.Features.Infra.Cloud.Cloudflare;
using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed class CloudServices
{
    public static IReadOnlyList<ResourceHandler> DefaultHandlers { get; } =
    [
        new VpcHandler(),
        new SshKeyHandler(),
        new DropletHandler(),
        new FirewallHandler(),
        new SpacesBucketHandler(),
        new DatabaseClusterHandler(),
        new DatabaseHandler(),
        new ZoneHandler(),
        new RecordHandler(),
    ];

    public CloudServices(HttpClient http, IReadOnlyList<ResourceHandler>? handlers = null)
    {
        Http = http;
        Handlers = handlers ?? DefaultHandlers;
        Live = new CloudLiveValues(State);
        Planner = new CloudPlanner(this);
        Applier = new CloudApplier(this);
    }

    public static CloudServices CreateDefault()
    {
        var http = new HttpClient(new TrafficHandler(new HttpClientHandler()));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("repo-infra/0.1");
        return new CloudServices(http);
    }

    public HttpClient Http { get; }

    public IReadOnlyList<ResourceHandler> Handlers { get; }

    public CloudState State { get; } = new();

    public CloudLiveValues Live { get; }

    public CloudPlanner Planner { get; }

    public CloudApplier Applier { get; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public bool Handles(string provider) => Handlers.Any(handler => handler.Provider == provider);

    public ResourceHandler? Handler(string provider, string kind) => Handlers.FirstOrDefault(handler => handler.Provider == provider && handler.Kind == kind);

    public Task<CloudInventory> ScanAsync(PlanRun run, InfraLog log, bool allKinds, CancellationToken cancellationToken) =>
        CloudInventory.ScanAsync(this, run, log, allKinds, cancellationToken);
}