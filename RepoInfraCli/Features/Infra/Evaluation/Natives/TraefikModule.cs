using System.Globalization;
using System.Text.RegularExpressions;
using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed partial class TraefikModule : INativeModule
{
    const string DefaultEntryPoint = "websecure";

    static readonly ObjectShape ResolverOptions = Object("ResolverOptions",
        Required("email", Shapes.String),
        Required("storage", Shapes.String),
        Optional("dns_challenge", Shapes.String));

    static readonly ObjectShape StaticConfig = Object("StaticConfig",
        Required("entrypoints", Map(Object("EntryPoint", Required("address", Shapes.String), Optional("trusted_ips", StringList)))),
        Optional("resolvers", List(Resolver)),
        Optional("docker", Object("DockerProvider", Optional("exposed_by_default", Shapes.Boolean), Optional("network", Network))),
        Optional("log_level", Literal("DEBUG", "INFO", "WARN", "ERROR")));

    static readonly ObjectShape RouteOptions = Object("RouteOptions",
        Required("host", Union(Shapes.String, Domain)),
        Required("port", Number),
        Optional("router", Shapes.String),
        Optional("entrypoint", Shapes.String),
        Optional("resolver", Resolver),
        Optional("middlewares", StringList));

    public string Name => "traefik";

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("resolver", context.Named("traefik.resolver", (name, location) => context.Then(options => DeclareResolver(context, name, options, location)))),
        KeyValuePair.Create("args", context.Function(args => Args(context, EvaluationContext.Argument(args, 0)))),
        KeyValuePair.Create("labels", context.Function(args => Labels(context, EvaluationContext.Argument(args, 0)))),
    ]);

    static LuaValue DeclareResolver(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        context.CheckOptions($"traefik.resolver \"{name}\"", options, ResolverOptions, location);
        var id = NodeId.Of("traefik.resolver", name);
        context.Recorder.AddComponent(new ComponentNode(id, "traefik", NodeKinds.Resolver, name, context.Converter.ConvertMap(options), false, location));
        return context.Node(id, NodeKinds.Resolver, [KeyValuePair.Create("name", EvaluationContext.Text(name))]);
    }

    static LuaValue Args(EvaluationContext context, LuaValue config)
    {
        var location = context.Here();
        if (!context.CheckOptions("traefik.args", config, StaticConfig, location))
            return context.List([]);

        var map = context.Converter.ConvertMap(config);
        var args = new List<string>();

        if (map["entrypoints"] is MapValue entryPoints)
        {
            foreach (var (name, value) in entryPoints.Entries)
            {
                if (value is not MapValue entryPoint)
                    continue;
                args.Add($"--entrypoints.{name}.address={entryPoint.GetString("address")}");
                if (entryPoint["trusted_ips"] is ListValue { Items.Length: > 0 } trusted)
                    args.Add($"--entrypoints.{name}.forwardedHeaders.trustedIPs={string.Join(',', trusted.Items.Select(item => item.Display()))}");
            }
        }

        foreach (var resolver in context.Items(context.Raw(config, "resolvers")))
        {
            if (context.NodeOf(resolver) is not { } id || context.Recorder.Components.FirstOrDefault(component => component.Id == id) is not { } component)
                continue;
            var prefix = $"--certificatesresolvers.{component.Name}.acme";
            args.Add($"{prefix}.email={component.Options.GetString("email")}");
            args.Add($"{prefix}.storage={component.Options.GetString("storage")}");
            args.Add(component.Options.GetString("dns_challenge") is { } provider
                ? $"{prefix}.dnschallenge.provider={provider}"
                : $"{prefix}.tlschallenge=true");
        }

        var docker = context.Raw(config, "docker");
        if (!docker.IsNil)
        {
            args.Add("--providers.docker=true");
            if (context.Raw(docker, "exposed_by_default") is { Type: LuaValueType.Boolean } exposed)
                args.Add($"--providers.docker.exposedbydefault={(exposed.Read<bool>() ? "true" : "false")}");
            if (context.RawString(context.Raw(docker, "network"), "name") is { } network)
                args.Add($"--providers.docker.network={network}");
        }

        if (map.GetString("log_level") is { } level)
            args.Add($"--log.level={level}");

        return context.List(args.Select(EvaluationContext.Text));
    }

    static LuaValue Labels(EvaluationContext context, LuaValue route)
    {
        var location = context.Here();
        if (!context.CheckOptions("traefik.labels", route, RouteOptions, location))
            return context.Map([]);

        var hostValue = context.Raw(route, "host");
        var host = hostValue.Type == LuaValueType.String ? hostValue.Read<string>() : context.RawString(hostValue, "hostname") ?? "";
        var router = context.RawString(route, "router") ?? RouterName(host);
        var entryPoint = context.RawString(route, "entrypoint") ?? DefaultEntryPoint;
        var port = context.Raw(route, "port").Read<double>().ToString(CultureInfo.InvariantCulture);

        var labels = new List<KeyValuePair<string, string>>
        {
            new("traefik.enable", "true"),
            new($"traefik.http.routers.{router}.rule", $"Host(`{host}`)"),
            new($"traefik.http.routers.{router}.entrypoints", entryPoint),
            new($"traefik.http.routers.{router}.tls", "true"),
            new($"traefik.http.services.{router}.loadbalancer.server.port", port),
        };
        if (context.RawString(context.Raw(route, "resolver"), "name") is { } resolver)
            labels.Add(new($"traefik.http.routers.{router}.tls.certresolver", resolver));
        if (context.Items(context.Raw(route, "middlewares")) is { Count: > 0 } middlewares)
            labels.Add(new($"traefik.http.routers.{router}.middlewares", string.Join(',', middlewares.Select(item => item.Read<string>()))));

        return context.Map(labels.Select(label => KeyValuePair.Create(label.Key, EvaluationContext.Text(label.Value))));
    }

    static string RouterName(string host) => NonIdentifier().Replace(host, "-").Trim('-');

    [GeneratedRegex("[^A-Za-z0-9-]+")]
    private static partial Regex NonIdentifier();
}
