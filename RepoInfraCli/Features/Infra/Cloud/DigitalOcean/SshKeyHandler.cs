using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class SshKeyHandler : ResourceHandler
{
    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => "ssh_key";

    public override int DestroyRank => 5;

    public override IReadOnlyList<FieldSpec> Fields { get; } = [new("public_key", FieldMode.Replace)];

    public override string LiveName(ResourceNode node, Marker marker) => marker.PrefixedName(node.Name);

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        [.. (await session.DigitalOcean.ListAsync("v2/account/keys?per_page=200", "ssh_keys", cancellationToken)).Select(key => Live(key, marker))];

    static LiveResource Live(JsonElement key, Marker marker)
    {
        var name = key.Text("name") ?? "";
        return new LiveResource("ssh_key", key.Text("id") ?? "", name, OwnerMark.FromPrefixedName(name, marker))
        {
            Fields = new Dictionary<string, string> { ["public_key"] = Canonical(key.Text("public_key")) },
            Outputs = new Dictionary<string, string> { ["id"] = key.Text("id") ?? "", ["fingerprint"] = key.Text("fingerprint") ?? "" },
            Raw = key,
        };
    }

    public override LiveResource? FindUnowned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker)
    {
        var resources = pool.ToList();
        if (base.FindUnowned(node, resources, marker) is { } named)
            return named;
        var key = node.Options["public_key"] is FileRef file && File.Exists(file.Resolved) ? Canonical(File.ReadAllText(file.Resolved)) : null;
        return key is null ? null : resources.FirstOrDefault(live => live.Mark is null && live.Fields["public_key"] == key);
    }

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values) =>
        new Dictionary<string, string?> { ["public_key"] = Canonical(PublicKey(node, values)) };

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["name"] = context.Marker.PrefixedName(context.Node.Name),
            ["public_key"] = PublicKey(context.Node, context.Values),
        };
        return Live(await context.Session.DigitalOcean.PostAsync("v2/account/keys", body, "ssh_key", cancellationToken), context.Marker);
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken) =>
        changed.Contains(MarkerField) ? await MarkAsync(context.Session, live, context.Mark, cancellationToken) : live;

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.DigitalOcean.DeleteAsync($"v2/account/keys/{live.Id}", cancellationToken);

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        var name = new Marker(mark.Owner, null).PrefixedName(mark.Name);
        await session.DigitalOcean.SendAsync(HttpMethod.Put, $"v2/account/keys/{live.Id}", new JsonObject { ["name"] = name }, cancellationToken);
        return live with { Name = name, Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"rename ssh key {live.Name} -> {marker.PrefixedName(mark.Name)}";

    static string PublicKey(ResourceNode node, CloudValues values) =>
        node.Options["public_key"] is { } key ? values.Text(key) ?? "" : throw new ResourcePlanException("public_key is required");

    public static string Canonical(string? key) => string.Join(' ', (key ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2));
}
