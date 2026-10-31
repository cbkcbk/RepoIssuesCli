using System.Text.Json.Nodes;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using RepoInfraCli.Tests.Hosts;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Cloud;

public sealed class CloudHarness : IDisposable
{
    public const string DigitalOcean = "https://api.digitalocean.com/v2/";
    public const string Zones = "https://api.cloudflare.com/client/v4/zones?per_page=50&page=1";
    public const string ExampleZone = "023e105f4ecef8ad9ca31a8372d0c353";
    public const string ExampleRecords = $"https://api.cloudflare.com/client/v4/zones/{ExampleZone}/dns_records?per_page=100&page=1";

    public TempRepo Repo { get; } = new();

    public FakeCloud Http { get; } = new();

    public FakeConnector Connector { get; set; } = new();

    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal)
    {
        ["INFRA_SECRET_DIGITALOCEAN_TOKEN"] = "do-token",
        ["INFRA_SECRET_CLOUDFLARE_TOKEN"] = "cf-token",
        ["INFRA_SECRET_DIGITALOCEAN_SPACES_ACCESS_KEY"] = "DO00EXAMPLEACCESS",
        ["INFRA_SECRET_DIGITALOCEAN_SPACES_SECRET_KEY"] = "spaces-secret",
    };

    public CloudServices Cloud { get; private set; } = null!;

    public const string Providers = """
        local digitalocean = require("@infra/digitalocean")
        local cloudflare = require("@infra/cloudflare")
        local cloud = digitalocean.provider { token = infra.secret "digitalocean/token" }
        local dns = cloudflare.provider { api_token = infra.secret "cloudflare/token" }

        """;

    public void Script(string body, bool prune = false) =>
        Repo.WriteInfra($$"""
                          local infra = require("@infra")
                          infra.project "test" { prune = {{(prune ? "true" : "false")}} }
                          {{Providers}}
                          {{body}}
                          """);

    public CloudHarness EmptyAccount()
    {
        foreach (var (path, property) in Lists)
            Http.Set("GET", $"{DigitalOcean}{path}?per_page=200", List(property));
        Http.On("GET", Zones, FakeCloud.Fixture("cf-zones.json"));
        Http.On("GET", ExampleRecords, """{"result":[],"result_info":{"page":1,"per_page":100,"count":0,"total_count":0,"total_pages":1},"success":true,"errors":[],"messages":[]}""");
        Http.OnPattern("GET", @"^https://\w+\.digitaloceanspaces\.com/$", """<?xml version="1.0" encoding="UTF-8"?><ListAllMyBucketsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Buckets></Buckets></ListAllMyBucketsResult>""");
        return this;
    }

    static readonly (string Path, string Property)[] Lists =
    [
        ("droplets", "droplets"), ("account/keys", "ssh_keys"), ("firewalls", "firewalls"), ("vpcs", "vpcs"), ("databases", "databases"),
    ];

    public CloudHarness Account(string path, string property, params JsonNode[] items)
    {
        Http.Set("GET", $"{DigitalOcean}{path}?per_page=200", List(property, items));
        return this;
    }

    public static string List(string property, params JsonNode[] items) =>
        new JsonObject { [property] = new JsonArray([.. items]), ["links"] = new JsonObject(), ["meta"] = new JsonObject { ["total"] = items.Length } }.ToJsonString();

    public static JsonObject Droplet(string name = "web-1", long id = 3164444, string region = "fra1", string size = "s-2vcpu-4gb", params string[] tags)
    {
        var droplet = JsonNode.Parse(FakeCloud.Fixture("do-droplet.json"))!.AsObject();
        droplet["id"] = id;
        droplet["name"] = name;
        droplet["size_slug"] = size;
        droplet["region"]!["slug"] = region;
        droplet["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode)JsonValue.Create(tag))]);
        return droplet;
    }

    public static JsonObject Fixture(string name) => JsonNode.Parse(FakeCloud.Fixture(name))!.AsObject();

    public RunResult Run(params string[] args) => Run(new TestConsole().Width(250), args);

    public RunResult Run(TestConsole output, params string[] args)
    {
        var errors = new TestConsole().Width(250);
        Cloud = new CloudServices(new HttpClient(Http)) { PollInterval = TimeSpan.Zero, WaitTimeout = TimeSpan.FromSeconds(5) };
        var secrets = new SecretEnvironment(name => Variables.GetValueOrDefault(name), Path.Combine(Repo.Root, "no-key"));
        var exitCode = InfraApp.Create(output, errors, secrets: secrets, services: services =>
        {
            services.RegisterInstance(Cloud);
            services.RegisterInstance<ILiveValues>(Cloud.Live);
            services.RegisterInstance<IResourcePlanner>(Cloud.Planner);
            services.RegisterInstance<IResourceApplier>(Cloud.Applier);
            services.RegisterInstance<IHostConnector>(Connector);
        }).Run([.. args, "--root", Repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    public static IReadOnlyList<string> Lines(string output) => [.. output.Split('\n').Select(line => line.TrimEnd())];

    public void Dispose() => Repo.Dispose();
}
