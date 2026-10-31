using NuLua;

namespace RepoInfraCli.Features.Infra.Evaluation;

public abstract record Shape(string Name);

public sealed record PrimitiveShape(LuaValueType Type, string TypeName) : Shape(TypeName);

public sealed record LiteralShape(IReadOnlyList<string> Values) : Shape(string.Join(" | ", Values.Select(value => $"\"{value}\"")));

public sealed record ListShape(Shape Item) : Shape($"{{ {Item.Name} }}");

public sealed record MapShape(Shape Value) : Shape($"{{ [string]: {Value.Name} }}");

public sealed record UnionShape(IReadOnlyList<Shape> Options) : Shape(string.Join(" | ", Options.Select(option => option.Name)));

public sealed record TagShape(string Tag, string TypeName) : Shape(TypeName);

public sealed record KindShape(string Kind, string TypeName, string Origin) : Shape(TypeName);

public sealed record FunctionShape() : Shape("function");

public sealed record AnyShape() : Shape("any");

public sealed record Field(string Name, Shape Shape, bool Required);

public sealed record ObjectShape(string TypeName, IReadOnlyList<Field> Fields) : Shape(TypeName)
{
    public Field? Find(string name) => Fields.FirstOrDefault(field => field.Name == name);
}

public static class Shapes
{
    public const string SecretTag = "__secret";
    public const string FileTag = "__file";
    public const string OutputTag = "__output";
    public const string NodeTag = "__node";
    public const string KindTag = "__kind";
    public const string HandlerTag = "__handler";
    public const string BuildTag = "__build";
    public const string RoleTag = "__role";

    public static readonly Shape String = new PrimitiveShape(LuaValueType.String, "string");
    public static readonly Shape Number = new PrimitiveShape(LuaValueType.Number, "number");
    public static readonly Shape Boolean = new PrimitiveShape(LuaValueType.Boolean, "boolean");
    public static readonly Shape Function = new FunctionShape();
    public static readonly Shape Any = new AnyShape();
    public static readonly Shape Secret = new TagShape(SecretTag, "Secret");
    public static readonly Shape File = new TagShape(FileTag, "FileRef");
    public static readonly Shape Output = new TagShape(OutputTag, "Output");
    public static readonly Shape Handler = new TagShape(HandlerTag, "Handler");
    public static readonly Shape Build = new TagShape(BuildTag, "Build");
    public static readonly Shape InputString = Union(String, Output);
    public static readonly Shape StringList = List(String);
    public static readonly Shape Network = Kind(NodeKinds.Network, "Network", "compose.network");
    public static readonly Shape Resolver = Kind(NodeKinds.Resolver, "Resolver", "traefik.resolver");
    public static readonly Shape Database = Kind(NodeKinds.Database, "Database", "postgres.database or a database cluster");
    public static readonly Shape Bucket = Kind(NodeKinds.Bucket, "Bucket", "spaces_bucket");
    public static readonly Shape Host = Kind(NodeKinds.Host, "Host", "infra.host or droplet:host");
    public static readonly Shape App = Kind(NodeKinds.App, "App", "infra.app");
    public static readonly Shape Role = Union(Function, Kind(NodeKinds.Role, "Role", "a role function or repo provider role"));
    public static readonly Shape Domain = Union(Kind(NodeKinds.Record, "Record", "zone.record"), Object("Domain", Required("hostname", String)));
    public static readonly Shape ServiceState = Literal("started", "stopped", "restarted", "reloaded", "running");

    public static readonly Shape FirewallRule = Object("FirewallRule",
        Optional("tcp", Union(Number, String)),
        Optional("udp", Union(Number, String)),
        Optional("icmp", Boolean),
        Optional("from", StringList),
        Optional("to", StringList));

    public static readonly Shape ComposeService = Object("ComposeService",
        Required("image", InputString),
        Optional("restart", Literal("no", "always", "on-failure", "unless-stopped")),
        Optional("ports", StringList),
        Optional("networks", List(Network)),
        Optional("volumes", StringList),
        Optional("environment", Map(Union(String, Output, Secret))),
        Optional("command", StringList),
        Optional("labels", Map(String)),
        Optional("healthcheck", Object("ComposeHealthcheck", Optional("http", String), Optional("test", StringList), Optional("interval", String))),
        Optional("depends_on", StringList));

    public static Shape List(Shape item) => new ListShape(item);

    public static Shape Map(Shape value) => new MapShape(value);

    public static Shape Union(params Shape[] options) => new UnionShape(options);

    public static Shape Literal(params string[] values) => new LiteralShape(values);

    public static Shape Kind(string kind, string typeName, string origin) => new KindShape(kind, typeName, origin);

    public static ObjectShape Object(string typeName, params Field[] fields) => new(typeName, fields);

    public static Field Required(string name, Shape shape) => new(name, shape, true);

    public static Field Optional(string name, Shape shape) => new(name, shape, false);

    public static Field Protect => Optional("protect", Boolean);
}

public static class NodeKinds
{
    public const string Network = "network";
    public const string Resolver = "resolver";
    public const string Database = "database";
    public const string Bucket = "bucket";
    public const string Host = "host";
    public const string App = "app";
    public const string Role = "role";
    public const string Record = "record";
    public const string Zone = "zone";
    public const string Build = "build";
    public const string Handler = "handler";
}
