using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class DigitalOceanModule : INativeModule
{
    public const string ProviderKind = "digitalocean";
    public const string SpacesAccessKey = "spaces_access_key";
    public const string SpacesSecretKey = "spaces_secret_key";

    static readonly ObjectShape ProviderOptions = Object("ProviderOptions",
        Required("token", Secret),
        Optional(SpacesAccessKey, Secret),
        Optional(SpacesSecretKey, Secret));

    static readonly ObjectShape SshKeyOptions = Object("SshKeyOptions", Required("public_key", Shapes.File), Protect);

    static readonly ObjectShape DropletOptions = Object("DropletOptions",
        Required("image", Shapes.String),
        Required("size", Shapes.String),
        Required("region", Shapes.String),
        Optional("ssh_keys", List(InputString)),
        Optional("tags", StringList),
        Optional("vpc", InputString),
        Optional("backups", Shapes.Boolean),
        Optional("monitoring", Shapes.Boolean),
        Protect);

    static readonly ObjectShape FirewallOptions = Object("FirewallOptions",
        Optional("droplets", List(InputString)),
        Optional("tags", StringList),
        Optional("inbound", List(FirewallRule)),
        Optional("outbound", List(FirewallRule)),
        Protect);

    static readonly ObjectShape VpcOptions = Object("VpcOptions",
        Required("region", Shapes.String),
        Optional("ip_range", Shapes.String),
        Protect);

    static readonly ObjectShape SpacesBucketOptions = Object("SpacesBucketOptions",
        Required("region", Shapes.String),
        Optional("acl", Literal("private", "public-read")),
        Optional("versioning", Shapes.Boolean),
        Optional("lifecycle", List(Object("LifecycleRule", Required("prefix", Shapes.String), Required("expire_days", Number)))),
        Protect);

    static readonly ObjectShape DatabaseClusterOptions = Object("DatabaseClusterOptions",
        Required("engine", Literal("pg")),
        Required("version", Shapes.String),
        Required("size", Shapes.String),
        Required("region", Shapes.String),
        Optional("nodes", Number),
        Optional("trusted_sources", List(InputString)),
        Protect);

    static readonly ObjectShape ManagedDatabaseOptions = Object("ManagedDatabaseOptions", Optional("user", Shapes.String));

    static readonly ObjectShape HostConnection = Object("HostConnection",
        Required("user", Shapes.String),
        Optional("port", Number),
        Optional("become", Shapes.Boolean),
        Optional("become_password", Secret));

    public string Name => ProviderKind;

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("provider", context.Function(args =>
            Provider(context, ProviderScope.Register(context, ProviderKind, EvaluationContext.Argument(args, 0), ProviderOptions, context.Here())))),
    ]);

    static LuaValue Provider(EvaluationContext context, ProviderScope scope) => context.Map(
    [
        Resource(context, "ssh_key", (name, options, location) => SshKey(context, scope, name, options, location)),
        Resource(context, "droplet", (name, options, location) => Droplet(context, scope, name, options, location)),
        Resource(context, "firewall", (name, options, location) =>
        {
            scope.Add("firewall", name, options, FirewallOptions, location);
            return LuaValue.Nil;
        }),
        Resource(context, "vpc", (name, options, location) => Vpc(context, scope, name, options, location)),
        Resource(context, "spaces_bucket", (name, options, location) => Bucket(context, scope, name, options, location)),
        Resource(context, "database_cluster", (name, options, location) => Cluster(context, scope, name, options, location)),
        KeyValuePair.Create("existing", context.Map(
        [
            KeyValuePair.Create("database_cluster", context.Named("existing.database_cluster", (name, location) => ExistingCluster(context, scope, name, location))),
        ])),
    ]);

    static KeyValuePair<string, LuaValue> Resource(EvaluationContext context, string kind, Func<string, LuaValue, SourceLocation?, LuaValue> create) =>
        KeyValuePair.Create(kind, context.Named(kind, (name, location) => context.Then(options => create(name, options, location))));

    static LuaValue SshKey(EvaluationContext context, ProviderScope scope, string name, LuaValue options, SourceLocation? location)
    {
        var node = scope.Add("ssh_key", name, options, SshKeyOptions, location);
        return context.Node(node.Id, "ssh_key",
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("id", context.Output(node.Id, "id")),
            KeyValuePair.Create("fingerprint", context.Output(node.Id, "fingerprint")),
        ]);
    }

    static LuaValue Droplet(EvaluationContext context, ProviderScope scope, string name, LuaValue options, SourceLocation? location)
    {
        var node = scope.Add("droplet", name, options, DropletOptions, location);
        return context.Node(node.Id, "droplet",
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("id", context.Output(node.Id, "id")),
            KeyValuePair.Create("ipv4_address", context.Output(node.Id, "ipv4_address")),
            KeyValuePair.Create("ipv4_address_private", context.Output(node.Id, "ipv4_address_private")),
            KeyValuePair.Create("ipv6_address", context.Output(node.Id, "ipv6_address")),
            KeyValuePair.Create("host", context.Function(args => DropletHost(context, node, EvaluationContext.Argument(args, 1)))),
        ]);
    }

    static LuaValue DropletHost(EvaluationContext context, ResourceNode droplet, LuaValue connection)
    {
        var location = context.Here();
        context.CheckOptions($"droplet \"{droplet.Name}\":host", connection, HostConnection, location);
        var map = context.Converter.ConvertMap(connection);
        var id = NodeId.Of(NodeKinds.Host, droplet.Name);
        context.Recorder.Hosts.Add(new HostNode(
            id, droplet.Name, new OutputRef(droplet.Id, "ipv4_address"), map.GetString("user") ?? "", InfraModule.Port(map), map.GetBool("become"),
            map["become_password"] as SecretRef, droplet.Id, false, null, location));
        return HostTasks.For(context).Table(id, droplet.Name);
    }

    static LuaValue Vpc(EvaluationContext context, ProviderScope scope, string name, LuaValue options, SourceLocation? location)
    {
        var node = scope.Add("vpc", name, options, VpcOptions, location);
        return context.Node(node.Id, "vpc",
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("id", context.Output(node.Id, "id")),
        ]);
    }

    static LuaValue Bucket(EvaluationContext context, ProviderScope scope, string name, LuaValue options, SourceLocation? location)
    {
        var node = scope.Add("spaces_bucket", name, options, SpacesBucketOptions, location);
        return context.Node(node.Id, NodeKinds.Bucket,
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("endpoint", context.Output(node.Id, "endpoint")),
        ]);
    }

    static LuaValue Cluster(EvaluationContext context, ProviderScope scope, string name, LuaValue options, SourceLocation? location)
    {
        var node = scope.Add("database_cluster", name, options, DatabaseClusterOptions, location);
        var version = node.Options.GetString("version");
        return ClusterTable(context, node, name,
            context.Named("database", (database, databaseLocation) => context.Then(databaseOptions =>
            {
                context.CheckOptions($"database \"{database}\"", databaseOptions, ManagedDatabaseOptions, databaseLocation);
                var user = context.Converter.ConvertMap(databaseOptions).GetString("user") ?? database;
                return ManagedDatabase(context, scope, node, database, databaseOptions, user, version, false, databaseLocation);
            })));
    }

    static LuaValue ExistingCluster(EvaluationContext context, ProviderScope scope, string name, SourceLocation? location)
    {
        var node = scope.Add("database_cluster", name, LuaValue.Nil, null, location, existing: true);
        return ClusterTable(context, node, name,
            context.Named("database", (database, databaseLocation) =>
                ManagedDatabase(context, scope, node, database, LuaValue.Nil, database, null, true, databaseLocation)));
    }

    static LuaValue ClusterTable(EvaluationContext context, ResourceNode node, string name, LuaValue database) =>
        context.Node(node.Id, "database_cluster",
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("id", context.Output(node.Id, "id")),
            KeyValuePair.Create("host", context.Output(node.Id, "host")),
            KeyValuePair.Create("database", database),
        ]);

    static LuaValue ManagedDatabase(
        EvaluationContext context, ProviderScope scope, ResourceNode cluster, string name, LuaValue options, string user, string? version, bool existing,
        SourceLocation? location)
    {
        var node = scope.Add("database", name, options, null, location, cluster.Id, existing, $"{cluster.Name}/{name}");
        context.Recorder.Databases[node.Id] = new DatabaseInfo(node.Id, name, user, version, LuaValue.Nil, LuaValue.Nil, null);
        return DatabaseValues.Table(context, node.Id, name, context.Output(cluster.Id, "host"));
    }
}