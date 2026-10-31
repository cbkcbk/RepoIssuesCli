using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class CloudflareModule : INativeModule
{
    public const string ProviderKind = "cloudflare";

    public static readonly IReadOnlyList<string> IpRanges =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
    ];

    static readonly ObjectShape ProviderOptions = Object("ProviderOptions", Required("api_token", Secret));

    static readonly ObjectShape RecordOptions = Object("RecordOptions",
        Required("type", Literal("A", "AAAA", "CNAME", "TXT", "MX", "CAA")),
        Required("content", InputString),
        Optional("proxied", Shapes.Boolean),
        Optional("ttl", Number),
        Protect);

    public string Name => ProviderKind;

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("ip_ranges", context.List(IpRanges.Select(EvaluationContext.Text))),
        KeyValuePair.Create("provider", context.Function(args =>
            Provider(context, ProviderScope.Register(context, ProviderKind, EvaluationContext.Argument(args, 0), ProviderOptions, context.Here())))),
    ]);

    static LuaValue Provider(EvaluationContext context, ProviderScope scope) => context.Map(
    [
        KeyValuePair.Create("zone", context.Named("zone", (name, location) => Zone(context, scope, name, location))),
    ]);

    static LuaValue Zone(EvaluationContext context, ProviderScope scope, string name, SourceLocation? location)
    {
        var zone = scope.Add(NodeKinds.Zone, name, LuaValue.Nil, null, location, existing: true);
        return context.Node(zone.Id, NodeKinds.Zone,
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("id", context.Output(zone.Id, "id")),
            KeyValuePair.Create("record", context.Named("record", (record, recordLocation) => context.Then(options =>
                Record(context, scope, zone, record, options, recordLocation)))),
        ]);
    }

    static LuaValue Record(EvaluationContext context, ProviderScope scope, ResourceNode zone, string name, LuaValue options, SourceLocation? location)
    {
        var hostname = Hostname(name, zone.Name);
        var type = context.RawString(options, "type") ?? "";
        var node = scope.Add(NodeKinds.Record, name, options, RecordOptions, location, zone.Id, key: $"{hostname}/{type}");
        return context.Node(node.Id, NodeKinds.Record,
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("hostname", EvaluationContext.Text(hostname)),
            KeyValuePair.Create("id", context.Output(node.Id, "id")),
        ]);
    }

    public static string Hostname(string record, string zone) => record == "@" ? zone : $"{record}.{zone}";
}
