using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class FirewallHandler : ResourceHandler
{
    const string AllPorts = "0";
    static readonly string[] Anywhere = ["0.0.0.0/0", "::/0"];

    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => "firewall";

    public override int DestroyRank => 1;

    public override IReadOnlyList<FieldSpec> Fields { get; } = [new("inbound"), new("outbound"), new("droplets"), new("tags")];

    public override string LiveName(ResourceNode node, Marker marker) => marker.PrefixedName(node.Name);

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        [.. (await session.DigitalOcean.ListAsync("v2/firewalls?per_page=200", "firewalls", cancellationToken)).Select(firewall => Live(firewall, marker))];

    static LiveResource Live(JsonElement firewall, Marker marker)
    {
        var name = firewall.Text("name") ?? "";
        return new LiveResource("firewall", firewall.Text("id") ?? "", name, OwnerMark.FromPrefixedName(name, marker))
        {
            Fields = new Dictionary<string, string>
            {
                ["inbound"] = LiveRules(firewall.Items("inbound_rules"), "sources"),
                ["outbound"] = LiveRules(firewall.Items("outbound_rules"), "destinations"),
                ["droplets"] = CloudValues.Join(firewall.Strings("droplet_ids"))!,
                ["tags"] = CloudValues.Join(firewall.Strings("tags"))!,
            },
            Outputs = new Dictionary<string, string> { ["id"] = firewall.Text("id") ?? "" },
            Raw = firewall,
        };
    }

    static string LiveRules(IEnumerable<JsonElement> rules, string side) =>
        CloudValues.Join(rules.Select(rule =>
        {
            var protocol = rule.Text("protocol") ?? "";
            var targets = rule.At(side) ?? default;
            var text = Rule(protocol, protocol == "icmp" ? AllPorts : rule.Text("ports") ?? AllPorts, targets.ValueKind == JsonValueKind.Object ? targets.Strings("addresses") : []);
            if (targets.ValueKind == JsonValueKind.Object && targets.Strings("droplet_ids") is { Count: > 0 } droplets)
                text += $" droplets {CloudValues.Join(droplets)}";
            if (targets.ValueKind == JsonValueKind.Object && targets.Strings("tags") is { Count: > 0 } tags)
                text += $" tags {CloudValues.Join(tags)}";
            return text;
        }))!;

    static string Rule(string protocol, string ports, IEnumerable<string> addresses) => $"{protocol} {ports} {CloudValues.Join(addresses)}";

    sealed record RuleSpec(string Protocol, string Ports, IReadOnlyList<string> Addresses);

    static IReadOnlyList<RuleSpec> Rules(InfraValue? value, string side)
    {
        if (value is not ListValue list)
            return side == "to" ? [new("tcp", AllPorts, Anywhere), new("udp", AllPorts, Anywhere), new("icmp", AllPorts, Anywhere)] : [];

        var rules = new List<RuleSpec>();
        foreach (var rule in list.Items.OfType<MapValue>())
        {
            var addresses = rule[side] is ListValue targets ? targets.Items.Select(item => item.Display()).ToList() : [.. Anywhere];
            foreach (var protocol in (string[])["tcp", "udp"])
            {
                if (rule[protocol] is { } ports)
                    rules.Add(new RuleSpec(protocol, ports.Display() is "all" or "0" ? AllPorts : ports.Display(), addresses));
            }

            if (rule.GetBool("icmp"))
                rules.Add(new RuleSpec("icmp", AllPorts, addresses));
        }

        return rules;
    }

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values)
    {
        var options = node.Options;
        return new Dictionary<string, string?>
        {
            ["inbound"] = CloudValues.Join(Rules(options["inbound"], "from").Select(rule => Rule(rule.Protocol, rule.Ports, rule.Addresses))),
            ["outbound"] = CloudValues.Join(Rules(options["outbound"], "to").Select(rule => Rule(rule.Protocol, rule.Ports, rule.Addresses))),
            ["droplets"] = CloudValues.Join(values.Items(options["droplets"])),
            ["tags"] = CloudValues.Join(values.Items(options["tags"])),
        };
    }

    static JsonObject Body(string name, ResourceNode node, CloudValues values)
    {
        JsonNode RuleBody(RuleSpec rule, string key)
        {
            var body = new JsonObject { ["protocol"] = rule.Protocol };
            if (rule.Protocol != "icmp")
                body["ports"] = rule.Ports;
            body[key] = new JsonObject { ["addresses"] = DigitalOceanClient.Array(rule.Addresses) };
            return body;
        }

        JsonArray Section(string option, string side, string key) => new([.. Rules(node.Options[option], side).Select(rule => RuleBody(rule, key))]);

        return new JsonObject
        {
            ["name"] = name,
            ["inbound_rules"] = Section("inbound", "from", "sources"),
            ["outbound_rules"] = Section("outbound", "to", "destinations"),
            ["droplet_ids"] = DigitalOceanClient.Numbers(values.RequireItems(node.Options["droplets"], $"firewall {node.Name} droplets")),
            ["tags"] = DigitalOceanClient.Array(values.RequireItems(node.Options["tags"], $"firewall {node.Name} tags")),
        };
    }

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var body = Body(context.Marker.PrefixedName(context.Node.Name), context.Node, context.Values);
        return Live(await context.Session.DigitalOcean.PostAsync("v2/firewalls", body, "firewall", cancellationToken), context.Marker);
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken)
    {
        var body = Body(context.Marker.PrefixedName(context.Node.Name), context.Node, context.Values);
        var updated = await context.Session.DigitalOcean.SendAsync(HttpMethod.Put, $"v2/firewalls/{live.Id}", body, cancellationToken);
        return updated?.At("firewall") is { } firewall ? Live(firewall, context.Marker) : live;
    }

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.DigitalOcean.DeleteAsync($"v2/firewalls/{live.Id}", cancellationToken);

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        var name = mark.Marker.PrefixedName(mark.Name);
        var body = new JsonObject { ["name"] = name };
        foreach (var key in (string[])["inbound_rules", "outbound_rules", "droplet_ids", "tags"])
        {
            if (live.Raw.ValueKind == JsonValueKind.Object && live.Raw.At(key) is { } value)
                body[key] = JsonNode.Parse(value.GetRawText());
        }

        await session.DigitalOcean.SendAsync(HttpMethod.Put, $"v2/firewalls/{live.Id}", body, cancellationToken);
        return live with { Name = name, Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"rename firewall {live.Name} -> {marker.PrefixedName(mark.Name)}";
}