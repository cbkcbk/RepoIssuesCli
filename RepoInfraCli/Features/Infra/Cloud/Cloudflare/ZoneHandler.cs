using System.Text.Json;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.Cloudflare;

public sealed class ZoneHandler : ResourceHandler
{
    public override string Provider => CloudflareModule.ProviderKind;

    public override string Kind => NodeKinds.Zone;

    public override bool HasMarker => false;

    public override bool Prunable => false;

    public override bool LookupOnly => true;

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        [.. (await session.Cloudflare.ListAsync("client/v4/zones", 50, cancellationToken)).Select(Live)];

    static LiveResource Live(JsonElement zone) =>
        new(NodeKinds.Zone, zone.Text("id") ?? "", zone.Text("name") ?? "", null)
        {
            Outputs = new Dictionary<string, string> { ["id"] = zone.Text("id") ?? "", ["name"] = zone.Text("name") ?? "" },
            Raw = zone,
        };

    public override async Task<LiveResource> BindAsync(ProviderSession session, ResourceNode node, LiveResource live, LiveResource? parent, CancellationToken cancellationToken)
    {
        var records = await session.Cloudflare.ListAsync($"client/v4/zones/{live.Id}/dns_records", 100, cancellationToken);
        return live with { Children = [.. records.Select(record => RecordHandler.Live(record, live))] };
    }
}
