using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.Cloudflare;

public sealed class RecordHandler : ResourceHandler
{
    const string AutomaticTtl = "1";

    public override string Provider => CloudflareModule.ProviderKind;

    public override string Kind => NodeKinds.Record;

    public override string ParentKind => NodeKinds.Zone;

    public override int DestroyRank => 0;

    public override IReadOnlyList<FieldSpec> Fields { get; } =
    [
        new("type", FieldMode.Replace),
        new("content"),
        new("proxied"),
        new("ttl"),
    ];

    public override string Display(ResourceNode node) => node.Id.Value[(node.Id.Value.IndexOf(':') + 1)..].Replace('/', ' ');

    public override string Display(LiveResource live) => $"{live.Outputs.GetValueOrDefault("hostname")} {live.Fields.GetValueOrDefault("type")}";

    public static LiveResource Live(JsonElement record, LiveResource zone)
    {
        var hostname = record.Text("name") ?? "";
        var name = hostname == zone.Name ? "@" : hostname.EndsWith("." + zone.Name, StringComparison.Ordinal) ? hostname[..^(zone.Name.Length + 1)] : hostname;
        var type = record.Text("type") ?? "";
        var content = record.Text("content") ?? "";
        if (type == "MX" && record.Text("priority") is { } priority)
            content = $"{priority} {content}";
        return new LiveResource(NodeKinds.Record, record.Text("id") ?? "", name, OwnerMark.Parse(record.Text("comment")))
        {
            Fields = new Dictionary<string, string>
            {
                ["type"] = type,
                ["content"] = content,
                ["proxied"] = record.Text("proxied") ?? "false",
                ["ttl"] = record.Text("ttl") ?? AutomaticTtl,
            },
            Outputs = new Dictionary<string, string> { ["id"] = record.Text("id") ?? "", ["hostname"] = hostname },
            Parent = zone.Id,
            Raw = record,
        };
    }

    static bool SameType(ResourceNode node, LiveResource live) => live.Fields.GetValueOrDefault("type") == node.Options.GetString("type");

    public override LiveResource? FindOwned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker) =>
        base.FindOwned(node, pool.Where(live => SameType(node, live)), marker);

    public override LiveResource? FindUnowned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker) =>
        pool.FirstOrDefault(live => live.Mark is null && live.Name == node.Name && SameType(node, live));

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values)
    {
        var proxied = node.Options.GetBool("proxied");
        return new Dictionary<string, string?>
        {
            ["type"] = Option(node, "type"),
            ["content"] = node.Options["content"] is { } content ? values.Text(content) : "",
            ["proxied"] = proxied ? "true" : "false",
            ["ttl"] = proxied ? AutomaticTtl : node.Options["ttl"]?.Display() ?? AutomaticTtl,
        };
    }

    JsonObject Body(ResourceContext context)
    {
        var node = context.Node;
        var desired = Desired(node, context.Values);
        var type = desired["type"]!;
        var content = context.Values.Require(node.Options["content"]!, $"record {Display(node)} content");
        var body = new JsonObject
        {
            ["type"] = type,
            ["name"] = CloudflareModule.Hostname(node.Name, context.Parent?.Name ?? ""),
            ["proxied"] = desired["proxied"] == "true",
            ["ttl"] = int.Parse(desired["ttl"]!, System.Globalization.CultureInfo.InvariantCulture),
            ["comment"] = context.Mark.Text,
        };
        if (type == "MX" && content.Split(' ', 2) is [var priority, var target] && int.TryParse(priority, out var number))
        {
            body["priority"] = number;
            content = target;
        }
        body["content"] = content;
        return body;
    }

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var zone = context.Parent ?? throw new CloudApiException($"zone of record {Display(context.Node)} is not known");
        var record = await context.Session.Cloudflare.ResultAsync(HttpMethod.Post, $"client/v4/zones/{zone.Id}/dns_records", Body(context), cancellationToken);
        return Live(record, zone);
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken)
    {
        var zone = context.Parent ?? throw new CloudApiException($"zone of record {Display(context.Node)} is not known");
        var record = await context.Session.Cloudflare.ResultAsync(HttpMethod.Patch, $"client/v4/zones/{zone.Id}/dns_records/{live.Id}", Body(context), cancellationToken);
        return Live(record, zone);
    }

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.Cloudflare.SendAsync(HttpMethod.Delete, $"client/v4/zones/{live.Parent}/dns_records/{live.Id}", null, cancellationToken);

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        await session.Cloudflare.SendAsync(HttpMethod.Patch, $"client/v4/zones/{live.Parent}/dns_records/{live.Id}", new JsonObject { ["comment"] = mark.Text }, cancellationToken);
        return live with { Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"set the comment of record {Display(live)} to {mark.Text}";
}
