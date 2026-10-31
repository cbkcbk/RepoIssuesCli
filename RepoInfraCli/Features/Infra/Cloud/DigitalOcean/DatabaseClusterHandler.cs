using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class DatabaseClusterHandler : ResourceHandler
{
    public const string KindName = "database_cluster";

    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => KindName;

    public override bool UniqueNames => true;

    public override int DestroyRank => 3;

    public override IReadOnlyList<FieldSpec> Fields { get; } =
    [
        new("engine", FieldMode.Replace),
        new("region", FieldMode.Replace),
        new("version", FieldMode.CreateOnly),
        new("size"),
        new("nodes"),
        new("trusted_sources"),
    ];

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        [.. (await session.DigitalOcean.ListAsync("v2/databases?per_page=200", "databases", cancellationToken)).Select(Live)];

    static LiveResource Live(JsonElement cluster)
    {
        var id = cluster.Text("id") ?? "";
        var name = cluster.Text("name") ?? "";
        var tags = cluster.Strings("tags");
        return new LiveResource(KindName, id, name, OwnerMark.FromTags(tags, name))
        {
            Fields = new Dictionary<string, string>
            {
                ["engine"] = cluster.Text("engine") ?? "",
                ["region"] = cluster.Text("region") ?? "",
                ["version"] = cluster.Text("version") ?? "",
                ["size"] = cluster.Text("size") ?? "",
                ["nodes"] = cluster.Text("num_nodes") ?? "",
            },
            Outputs = new Dictionary<string, string>
            {
                ["id"] = id,
                ["host"] = cluster.Text("connection", "host") ?? "",
                ["port"] = cluster.Text("connection", "port") ?? "",
            },
            Tags = tags,
            Children = [.. cluster.Strings("db_names").Select(database => new LiveResource(DatabaseHandler.KindName, $"{id}/{database}", database, null) { Parent = id, Raw = cluster })],
            Raw = cluster,
        };
    }

    public override async Task<LiveResource> BindAsync(ProviderSession session, ResourceNode node, LiveResource live, LiveResource? parent, CancellationToken cancellationToken)
    {
        if (node.Existing || node.Options["trusted_sources"] is null)
            return live;
        return await WithSourcesAsync(session, live, cancellationToken);
    }

    static async Task<LiveResource> WithSourcesAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken)
    {
        var rules = (await session.DigitalOcean.GetAsync($"v2/databases/{live.Id}/firewall", cancellationToken)).Items("rules");
        var sources = CloudValues.Join(rules.Select(rule => $"{rule.Text("type")}:{rule.Text("value")}"))!;
        return live with { Fields = new Dictionary<string, string>(live.Fields) { ["trusted_sources"] = sources } };
    }

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values)
    {
        var desired = new Dictionary<string, string?>
        {
            ["engine"] = Option(node, "engine"),
            ["region"] = Option(node, "region"),
            ["version"] = Option(node, "version"),
            ["size"] = Option(node, "size"),
            ["nodes"] = node.Options["nodes"]?.Display() ?? "1",
        };
        if (node.Options["trusted_sources"] is { } sources)
            desired["trusted_sources"] = CloudValues.Join(values.Items(sources).Select(source => source is null ? null : Source(source)));
        return desired;
    }

    static string Source(string value) =>
        value.All(char.IsAsciiDigit) ? $"droplet:{value}"
        : IPNetwork.TryParse(value, out _) || IPAddress.TryParse(value, out _) ? $"ip_addr:{value}"
        : $"tag:{value}";

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var (node, client) = (context.Node, context.Session.DigitalOcean);
        var body = new JsonObject
        {
            ["name"] = node.Name,
            ["engine"] = Option(node, "engine"),
            ["version"] = Option(node, "version"),
            ["region"] = Option(node, "region"),
            ["size"] = Option(node, "size"),
            ["num_nodes"] = (int)(node.Options["nodes"] is NumberValue nodes ? nodes.Value : 1),
            ["tags"] = DigitalOceanClient.Array(context.Mark.Tags),
        };
        var created = await client.PostAsync("v2/databases", body, "database", cancellationToken);
        var id = created.Text("id");
        var ready = await context.Session.WaitAsync($"database cluster {node.Name} to come online", async token =>
        {
            var cluster = (await client.GetAsync($"v2/databases/{id}", token)).At("database");
            return cluster is { } current && current.Text("status") == "online" ? current : (JsonElement?)null;
        }, cancellationToken);

        var live = Live(ready);
        if (node.Options["trusted_sources"] is not null)
        {
            await PutSourcesAsync(context, live, cancellationToken);
            live = await WithSourcesAsync(context.Session, live, cancellationToken);
        }
        return live;
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken)
    {
        var client = context.Session.DigitalOcean;
        var node = context.Node;
        if (changed.Contains("size") || changed.Contains("nodes"))
        {
            var body = new JsonObject
            {
                ["size"] = Option(node, "size"),
                ["num_nodes"] = (int)(node.Options["nodes"] is NumberValue nodes ? nodes.Value : 1),
            };
            await client.SendAsync(HttpMethod.Put, $"v2/databases/{live.Id}/resize", body, cancellationToken);
        }
        if (changed.Contains("trusted_sources"))
            await PutSourcesAsync(context, live, cancellationToken);
        if (changed.Contains(MarkerField))
            live = await MarkAsync(context.Session, live, context.Mark, cancellationToken);

        var refreshed = Live((await client.GetAsync($"v2/databases/{live.Id}", cancellationToken)).At("database")!.Value);
        return node.Options["trusted_sources"] is null ? refreshed : await WithSourcesAsync(context.Session, refreshed, cancellationToken);
    }

    static Task PutSourcesAsync(ResourceContext context, LiveResource live, CancellationToken cancellationToken)
    {
        var sources = context.Values.RequireItems(context.Node.Options["trusted_sources"], $"database cluster {context.Node.Name} trusted_sources");
        var rules = new JsonArray([
            .. sources.Select(Source).Select(source => (JsonNode)new JsonObject
            {
                ["type"] = source[..source.IndexOf(':')],
                ["value"] = source[(source.IndexOf(':') + 1)..],
            }),
        ]);
        return context.Session.DigitalOcean.SendAsync(HttpMethod.Put, $"v2/databases/{live.Id}/firewall", new JsonObject { ["rules"] = rules }, cancellationToken);
    }

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.DigitalOcean.DeleteAsync($"v2/databases/{live.Id}", cancellationToken);

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        await session.DigitalOcean.SyncTagsAsync("database", live.Id, live.Tags, [.. live.Tags.Where(tag => !OwnerMark.IsMarkerTag(tag)), .. mark.Tags], cancellationToken);
        return live with { Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"tag database cluster {live.Name} with {string.Join(", ", mark.Tags)}";
}
