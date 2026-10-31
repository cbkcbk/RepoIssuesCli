using System.Globalization;
using System.Security;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class SpacesBucketHandler : ResourceHandler
{
    public const string KindName = "spaces_bucket";
    public const string MarkerObject = ".infra-owner";
    const string AllUsers = "http://acs.amazonaws.com/groups/global/AllUsers";

    public static readonly IReadOnlyList<string> Regions = ["nyc3", "ams3", "sfo2", "sfo3", "sgp1", "fra1", "syd1", "blr1", "lon1", "tor1", "atl1"];

    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => KindName;

    public override bool UniqueNames => true;

    public override int DestroyRank => 4;

    public override IReadOnlyList<FieldSpec> Fields { get; } =
    [
        new("region", FieldMode.Replace),
        new("acl"),
        new("versioning"),
        new("lifecycle"),
    ];

    public override async Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken)
    {
        var spaces = session.Spaces;
        var buckets = new List<LiveResource>();
        foreach (var region in session.SpacesRegions.Order(StringComparer.Ordinal))
        {
            foreach (var name in await spaces.ListBucketsAsync(region, cancellationToken))
            {
                if (buckets.Any(bucket => bucket.Name == name))
                    continue;
                var mark = OwnerMark.Parse(await spaces.GetAsync(region, name, MarkerObject, "", cancellationToken));
                buckets.Add(Live(name, region, mark));
            }
        }
        return buckets;
    }

    static LiveResource Live(string name, string region, OwnerMark? mark) =>
        new(KindName, name, name, mark)
        {
            Fields = new Dictionary<string, string> { ["region"] = region },
            Outputs = new Dictionary<string, string> { ["name"] = name, ["endpoint"] = SpacesClient.Endpoint(region) },
        };

    public override async Task<LiveResource> BindAsync(ProviderSession session, ResourceNode node, LiveResource live, LiveResource? parent, CancellationToken cancellationToken)
    {
        var (spaces, region) = (session.Spaces, live.Fields["region"]);
        var versioning = SpacesClient.Parse(await spaces.GetAsync(region, live.Name, null, "versioning", cancellationToken) ?? "");
        var lifecycle = SpacesClient.Parse(await spaces.GetAsync(region, live.Name, null, "lifecycle", cancellationToken) ?? "");
        var acl = SpacesClient.Parse(await spaces.GetAsync(region, live.Name, null, "acl", cancellationToken) ?? "");

        var publicRead = acl.Descendants().Where(element => element.Name.LocalName == "Grant").Any(grant =>
            grant.Descendants().Any(element => element.Name.LocalName == "URI" && element.Value == AllUsers)
            && SpacesClient.Child(grant, "Permission") is "READ" or "FULL_CONTROL");
        var rules = lifecycle.Descendants().Where(element => element.Name.LocalName == "Rule").Select(rule =>
        {
            var filter = rule.Elements().FirstOrDefault(element => element.Name.LocalName == "Filter");
            var expiration = rule.Elements().FirstOrDefault(element => element.Name.LocalName == "Expiration");
            var prefix = SpacesClient.Child(rule, "Prefix") ?? (filter is null ? null : SpacesClient.Child(filter, "Prefix")) ?? "";
            var days = expiration is null ? null : SpacesClient.Child(expiration, "Days");
            return $"{prefix} {days}d";
        });

        return live with
        {
            Fields = new Dictionary<string, string>(live.Fields)
            {
                ["acl"] = publicRead ? "public-read" : "private",
                ["versioning"] = versioning.Descendants().Any(element => element.Name.LocalName == "Status" && element.Value == "Enabled") ? "true" : "false",
                ["lifecycle"] = CloudValues.Join(rules)!,
            },
        };
    }

    sealed record Rule(string Prefix, int Days);

    static IReadOnlyList<Rule> Rules(ResourceNode node) =>
        node.Options["lifecycle"] is ListValue list
            ? [.. list.Items.OfType<MapValue>().Select(rule => new Rule(rule.GetString("prefix") ?? "", rule["expire_days"] is NumberValue days ? (int)days.Value : 0))]
            : [];

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values) =>
        new Dictionary<string, string?>
        {
            ["region"] = Option(node, "region"),
            ["acl"] = node.Options.GetString("acl") ?? "private",
            ["versioning"] = CloudValues.Flag(node.Options, "versioning"),
            ["lifecycle"] = CloudValues.Join(Rules(node).Select(rule => $"{rule.Prefix} {rule.Days}d")),
        };

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var (node, spaces) = (context.Node, context.Session.Spaces);
        var region = Option(node, "region");
        await spaces.PutAsync(region, node.Name, null, "", null, Headers("x-amz-acl", node.Options.GetString("acl") ?? "private"), cancellationToken);
        var live = await MarkAsync(context.Session, Live(node.Name, region, null), context.Mark, cancellationToken);
        if (node.Options.GetBool("versioning"))
            await PutVersioningAsync(context, region, cancellationToken);
        if (Rules(node).Count > 0)
            await PutLifecycleAsync(context, region, cancellationToken);
        return await BindAsync(context.Session, node, live, null, cancellationToken);
    }

    public override async Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken)
    {
        var (node, spaces, region) = (context.Node, context.Session.Spaces, live.Fields["region"]);
        if (changed.Contains("acl"))
            await spaces.PutAsync(region, node.Name, null, "acl", null, Headers("x-amz-acl", node.Options.GetString("acl") ?? "private"), cancellationToken);
        if (changed.Contains("versioning"))
            await PutVersioningAsync(context, region, cancellationToken);
        if (changed.Contains("lifecycle"))
        {
            if (Rules(node).Count > 0)
                await PutLifecycleAsync(context, region, cancellationToken);
            else
                await spaces.DeleteAsync(region, node.Name, null, "lifecycle", cancellationToken);
        }
        if (changed.Contains(MarkerField))
            live = await MarkAsync(context.Session, live, context.Mark, cancellationToken);
        return await BindAsync(context.Session, node, live, null, cancellationToken);
    }

    static Task PutVersioningAsync(ResourceContext context, string region, CancellationToken cancellationToken)
    {
        var status = context.Node.Options.GetBool("versioning") ? "Enabled" : "Suspended";
        var xml = $"""<VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Status>{status}</Status></VersioningConfiguration>""";
        return context.Session.Spaces.PutAsync(region, context.Node.Name, null, "versioning", xml, null, cancellationToken);
    }

    static Task PutLifecycleAsync(ResourceContext context, string region, CancellationToken cancellationToken)
    {
        var rules = Rules(context.Node).Select((rule, index) =>
            $"<Rule><ID>infra-{index}</ID><Prefix>{SecurityElement.Escape(rule.Prefix)}</Prefix><Status>Enabled</Status>"
            + $"<Expiration><Days>{rule.Days.ToString(CultureInfo.InvariantCulture)}</Days></Expiration></Rule>");
        var xml = $"<LifecycleConfiguration>{string.Concat(rules)}</LifecycleConfiguration>";
        return context.Session.Spaces.PutAsync(region, context.Node.Name, null, "lifecycle", xml, null, cancellationToken);
    }

    public override async Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken)
    {
        var region = live.Fields["region"];
        await session.Spaces.DeleteAsync(region, live.Name, MarkerObject, "", cancellationToken);
        await session.Spaces.DeleteAsync(region, live.Name, null, "", cancellationToken);
    }

    public override async Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken)
    {
        var tagging = $"infra-owner={Uri.EscapeDataString(mark.Owner)}&infra-name={Uri.EscapeDataString(mark.Name)}";
        await session.Spaces.PutAsync(live.Fields["region"], live.Name, MarkerObject, "", mark.Text, Headers("x-amz-tagging", tagging), cancellationToken);
        return live with { Mark = mark };
    }

    public override string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"write {MarkerObject} ({mark.Text}) into bucket {live.Name}";

    static Dictionary<string, string> Headers(string name, string value) => new() { [name] = value };
}
