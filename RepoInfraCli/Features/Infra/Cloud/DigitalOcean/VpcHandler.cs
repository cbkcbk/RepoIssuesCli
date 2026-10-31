using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class VpcHandler : ResourceHandler
{
    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => "vpc";

    public override bool UniqueNames => true;

    public override int DestroyRank => 6;

    public override IReadOnlyList<FieldSpec> Fields { get; } = [new("region", FieldMode.Replace), new("ip_range", FieldMode.Replace)];

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        [.. (await session.DigitalOcean.ListAsync("v2/vpcs?per_page=200", "vpcs", cancellationToken)).Select(Live)];

    static LiveResource Live(JsonElement vpc) =>
        new(Kind: "vpc", Id: vpc.Text("id") ?? "", Name: vpc.Text("name") ?? "", Mark: OwnerMark.Parse(vpc.Text("description")))
        {
            Fields = new Dictionary<string, string> { ["region"] = vpc.Text("region") ?? "", ["ip_range"] = vpc.Text("ip_range") ?? "" },
            Outputs = new Dictionary<string, string> { ["id"] = vpc.Text("id") ?? "" },
            Raw = vpc,
        };

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values)
    {
        var desired = new Dictionary<string, string?> { ["region"] = Option(node, "region") };
        if (node.Options.GetString("ip_range") is { } range)
            desired["ip_range"] = range;
        return desired;
    }

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["name"] = context.Node.Name,
            ["region"] = Option(context.Node, "region"),
            ["description"] = context.Mark.Text,
        };
        if (context.Node.Options.GetString("ip_range") is { } range)
            body["ip_range"] = range;
        return Live(await context.Session.DigitalOcean.PostAsync("v2/vpcs", body, "vpc", cancellationToken));
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken) =>
        changed.Contains(MarkerField) ? await MarkAsync(context.Session, live, context.Mark, cancellationToken) : live;

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.DigitalOcean.DeleteAsync($"v2/vpcs/{live.Id}", cancellationToken);

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        await session.DigitalOcean.SendAsync(HttpMethod.Patch, $"v2/vpcs/{live.Id}", new JsonObject { ["description"] = mark.Text }, cancellationToken);
        return live with { Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"set vpc {live.Name} description to {mark.Text}";
}
