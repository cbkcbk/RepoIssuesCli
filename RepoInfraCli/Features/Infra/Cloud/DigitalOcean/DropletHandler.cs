using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class DropletHandler : ResourceHandler
{
    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => "droplet";

    public override int DestroyRank => 2;

    public override IReadOnlyList<FieldSpec> Fields { get; } =
    [
        new("region", FieldMode.Replace),
        new("image", FieldMode.Replace),
        new("vpc", FieldMode.Replace),
        new("size", FieldMode.CreateOnly),
        new("ssh_keys", FieldMode.CreateOnly),
        new("monitoring", FieldMode.CreateOnly),
        new("tags"),
        new("backups"),
    ];

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        [.. (await session.DigitalOcean.ListAsync("v2/droplets?per_page=200", "droplets", cancellationToken)).Select(Live)];

    public static LiveResource Live(JsonElement droplet)
    {
        var name = droplet.Text("name") ?? "";
        var tags = droplet.Strings("tags");
        var features = droplet.Strings("features");
        var fields = new Dictionary<string, string>
        {
            ["region"] = droplet.Text("region", "slug") ?? "",
            ["image"] = droplet.Text("image", "slug") ?? droplet.Text("image", "id") ?? "",
            ["size"] = droplet.Text("size_slug") ?? "",
            ["tags"] = CloudValues.Join(tags.Where(tag => !OwnerMark.IsMarkerTag(tag)))!,
            ["backups"] = features.Contains("backups") ? "true" : "false",
            ["monitoring"] = features.Contains("monitoring") ? "true" : "false",
        };
        if (droplet.Text("vpc_uuid") is { } vpc)
            fields["vpc"] = vpc;

        var outputs = new Dictionary<string, string> { ["id"] = droplet.Text("id") ?? "", ["name"] = name };
        Add(outputs, "ipv4_address", Address(droplet, "v4", "public"));
        Add(outputs, "ipv4_address_private", Address(droplet, "v4", "private"));
        Add(outputs, "ipv6_address", Address(droplet, "v6", "public"));
        return new LiveResource("droplet", droplet.Text("id") ?? "", name, OwnerMark.FromTags(tags, name))
        {
            Fields = fields,
            Outputs = outputs,
            Tags = tags,
            Raw = droplet,
        };
    }

    static void Add(Dictionary<string, string> outputs, string key, string? value)
    {
        if (value is not null)
            outputs[key] = value;
    }

    static string? Address(JsonElement droplet, string family, string type) =>
        droplet.Items("networks", family).FirstOrDefault(network => network.Text("type") == type) is { ValueKind: JsonValueKind.Object } network
            ? network.Text("ip_address")
            : null;

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values)
    {
        var options = node.Options;
        var desired = new Dictionary<string, string?>
        {
            ["region"] = Option(node, "region"),
            ["image"] = Option(node, "image"),
            ["size"] = Option(node, "size"),
            ["tags"] = CloudValues.Join(values.Items(options["tags"])),
        };
        foreach (var flag in (string[])["backups", "monitoring"])
        {
            if (options[flag] is not null)
                desired[flag] = CloudValues.Flag(options, flag);
        }
        if (options["vpc"] is { } vpc)
            desired["vpc"] = values.Text(vpc);
        if (options["ssh_keys"] is { } keys)
            desired["ssh_keys"] = CloudValues.Join(values.Items(keys));
        return desired;
    }

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var (node, values, client) = (context.Node, context.Values, context.Session.DigitalOcean);
        var options = node.Options;
        var body = new JsonObject
        {
            ["name"] = node.Name,
            ["region"] = Option(node, "region"),
            ["size"] = Option(node, "size"),
            ["image"] = Option(node, "image"),
            ["ssh_keys"] = DigitalOceanClient.Array(values.RequireItems(options["ssh_keys"], $"droplet {node.Name} ssh_keys")),
            ["backups"] = options.GetBool("backups"),
            ["monitoring"] = options.GetBool("monitoring"),
            ["ipv6"] = true,
            ["tags"] = DigitalOceanClient.Array(UserTags(node, values).Concat(context.Mark.Tags)),
        };
        if (options["vpc"] is { } vpc)
            body["vpc_uuid"] = values.Require(vpc, $"droplet {node.Name} vpc");

        var created = await client.PostAsync("v2/droplets", body, "droplet", cancellationToken);
        var id = created.Text("id");
        var ready = await context.Session.WaitAsync($"droplet {node.Name} to become active", async token =>
        {
            var droplet = (await client.GetAsync($"v2/droplets/{id}", token)).At("droplet");
            return droplet is { } current && current.Text("status") == "active" && Address(current, "v4", "public") is not null ? current : (JsonElement?)null;
        }, cancellationToken);
        return Live(ready);
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken)
    {
        var client = context.Session.DigitalOcean;
        if (changed.Contains("backups"))
        {
            var type = context.Node.Options.GetBool("backups") ? "enable_backups" : "disable_backups";
            await client.SendAsync(HttpMethod.Post, $"v2/droplets/{live.Id}/actions", new JsonObject { ["type"] = type }, cancellationToken);
        }
        if (changed.Contains("tags") || changed.Contains(MarkerField))
            await client.SyncTagsAsync("droplet", live.Id, live.Tags, [.. UserTags(context.Node, context.Values), .. context.Mark.Tags], cancellationToken);
        return Live((await client.GetAsync($"v2/droplets/{live.Id}", cancellationToken)).At("droplet")!.Value);
    }

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.DigitalOcean.DeleteAsync($"v2/droplets/{live.Id}", cancellationToken);

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        await session.DigitalOcean.SyncTagsAsync("droplet", live.Id, live.Tags, [.. live.Tags.Where(tag => !OwnerMark.IsMarkerTag(tag)), .. mark.Tags], cancellationToken);
        return live with { Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"tag droplet {live.Id} with {string.Join(", ", mark.Tags)}";

    static IEnumerable<string> UserTags(ResourceNode node, CloudValues values) => values.RequireItems(node.Options["tags"], $"droplet {node.Name} tags");
}
