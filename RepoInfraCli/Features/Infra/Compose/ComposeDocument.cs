using System.Text.RegularExpressions;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Compose;

public sealed record ComposeService(string Name, string? Image, string? HttpHealthcheck);

public sealed record ComposeFiles(
    string Project,
    string Directory,
    string Yaml,
    string Env,
    IReadOnlyList<string> Networks,
    IReadOnlyList<ComposeService> Services,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> SecretValues)
{
    public string YamlPath => $"{Directory}/compose.yaml";

    public string EnvPath => $"{Directory}/.env";
}

public sealed partial class ComposeDocument
{
    public const string HttpHealthcheckField = "x-infra-healthcheck-http";

    static readonly string[] ServiceFieldOrder = ["image", "restart", "command", "ports", "networks", "volumes", "environment", "labels", "healthcheck", "depends_on"];
    static readonly string[] StringOnlyFields = ["environment", "labels"];

    readonly Func<SecretRef, string> secrets;
    readonly ILiveValues live;
    readonly bool strict;
    readonly List<KeyValuePair<string, string>> variables = [];
    readonly List<string> unknown = [];
    readonly SortedSet<string> networks = new(StringComparer.Ordinal);

    ComposeDocument(Func<SecretRef, string> secrets, ILiveValues live, bool strict)
    {
        this.secrets = secrets;
        this.live = live;
        this.strict = strict;
    }

    public static ComposeFiles Render(string name, MapValue options, Func<SecretRef, string> secrets, ILiveValues live, bool strict) =>
        new ComposeDocument(secrets, live, strict).Build(name, options);

    public static string ProjectName(string name) => NotProjectCharacter().Replace(name.ToLowerInvariant(), "-").Trim('-') is { Length: > 0 } project ? project : "app";

    public static string NetworkName(NodeId network)
    {
        var value = network.Value;
        return value.LastIndexOf(':') is var colon and >= 0 ? value[(colon + 1)..] : value;
    }

    ComposeFiles Build(string name, MapValue options)
    {
        var project = ProjectName(name);
        var directory = (options.GetString("dir") ?? $"/opt/{project}").TrimEnd('/');
        var services = new List<ComposeService>();
        var serviceEntries = new List<KeyValuePair<string, YamlNode>>();

        foreach (var (serviceName, value) in (options["services"] as MapValue ?? MapValue.Empty).Entries)
        {
            var service = value as MapValue ?? MapValue.Empty;
            serviceEntries.Add(new(serviceName, Service(serviceName, service, out var spec)));
            services.Add(spec);
        }

        var document = new List<KeyValuePair<string, YamlNode>>
        {
            new("name", new YamlScalar(project)),
            new("services", new YamlMapping(serviceEntries)),
        };
        if (networks.Count > 0)
            document.Add(new("networks", new YamlMapping([.. networks.Select(network => KeyValuePair.Create<string, YamlNode>(network, new YamlMapping([new("external", new YamlScalar("true", false))])))])));
        if (options["volumes"] is MapValue { Entries.Count: > 0 } volumes)
            document.Add(new("volumes", new YamlMapping([.. volumes.Entries.Select(entry => KeyValuePair.Create(entry.Key, Volume(entry.Key, entry.Value)))])));

        return new ComposeFiles(
            project,
            directory,
            ComposeYaml.Write(new YamlMapping(document)),
            ComposeEnvFile.Write(variables),
            [.. networks],
            services,
            unknown,
            [.. variables.Select(variable => variable.Value).Where(value => value.Length > 0).Distinct()]);
    }

    YamlNode Service(string name, MapValue service, out ComposeService spec)
    {
        var entries = new List<KeyValuePair<string, YamlNode>>();
        string? http = null;

        foreach (var (field, value) in service.Entries.OrderBy(entry => Order(entry.Key)).ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string[] path = [name, field];
            switch (field)
            {
                case "networks":
                    entries.Add(new(field, new YamlSequence([.. Items(value).Select((item, index) => Network(item, [.. path, $"{index}"]))])));
                    break;
                case "healthcheck" when value is MapValue healthcheck:
                    http = healthcheck["http"] is { } url ? Text(url, [.. path, "http"]) : null;
                    var rest = healthcheck.Without("http");
                    if (rest.Entries.Count > 0)
                        entries.Add(new(field, Node(rest, path, quoteAll: false)));
                    break;
                default:
                    entries.Add(new(field, Node(value, path, StringOnlyFields.Contains(field))));
                    break;
            }
        }

        if (http is not null)
            entries.Add(new(HttpHealthcheckField, new YamlScalar(Escape(http))));

        var image = service["image"] is { } imageValue ? Peek(imageValue) : null;
        spec = new ComposeService(name, image, http);
        return new YamlMapping(entries);
    }

    static int Order(string field) => Array.IndexOf(ServiceFieldOrder, field) is var index and >= 0 ? index : ServiceFieldOrder.Length;

    static IEnumerable<InfraValue> Items(InfraValue value) => value is ListValue list ? list.Items : [value];

    YamlNode Network(InfraValue value, string[] path)
    {
        var name = value is NodeRef node ? NetworkName(node.Node) : Text(value, path);
        networks.Add(name);
        return new YamlScalar(name);
    }

    YamlNode Volume(string name, InfraValue value) => value switch
    {
        ListValue { Items.Length: 0 } or MapValue { Entries.Count: 0 } => new YamlMapping([]),
        _ => Node(value, ["volumes", name], quoteAll: false),
    };

    YamlNode Node(InfraValue value, string[] path, bool quoteAll) => value switch
    {
        MapValue map => new YamlMapping([.. map.Entries.Select(entry => KeyValuePair.Create(entry.Key, Node(entry.Value, [.. path, entry.Key], quoteAll)))]),
        ListValue list => new YamlSequence([.. list.Items.Select((item, index) => Node(item, [.. path, $"{index}"], quoteAll))]),
        NumberValue or BoolValue => new YamlScalar(value.Display(), quoteAll),
        SecretRef secret => Secret(path, secrets(secret)),
        OutputRef output => Output(output, path, quoteAll),
        _ => new YamlScalar(Escape(Plain(value))),
    };

    YamlNode Output(OutputRef output, string[] path, bool quoteAll)
    {
        if (live.Resolve(output) is not { } resolved)
            return Unknown(output, path);
        return output.Sensitive ? Secret(path, Plain(resolved)) : Node(resolved, path, quoteAll);
    }

    YamlNode Unknown(OutputRef output, string[] path)
    {
        var where = string.Join('.', path);
        if (strict)
            throw new TaskPlanException($"{where} is the {output.Field} of {output.Node}, which has no value yet; apply what produces it first, then apply again");
        unknown.Add(where);
        return new YamlScalar(InfraValue.UnknownDisplay);
    }

    YamlNode Secret(string[] path, string value)
    {
        var name = VariableName(path);
        variables.Add(new(name, value));
        return new YamlScalar($"${{{name}}}");
    }

    string Text(InfraValue value, string[] path) => value switch
    {
        SecretRef or OutputRef { Sensitive: true } => throw new TaskPlanException($"{string.Join('.', path)} can't be a secret"),
        OutputRef output => live.Resolve(output) is { } resolved ? Plain(resolved) : ((YamlScalar)Unknown(output, path)).Text,
        _ => Plain(value),
    };

    string? Peek(InfraValue value) => value switch
    {
        OutputRef { Sensitive: false } output => live.Resolve(output) is { } resolved ? Plain(resolved) : null,
        StringValue text => text.Value,
        _ => null,
    };

    static string Plain(InfraValue value) => value switch
    {
        StringValue text => text.Value,
        FileRef file => file.Path,
        NodeRef node => NetworkName(node.Node),
        _ => value.Display(),
    };

    static string Escape(string text) => text.Replace("$", "$$", StringComparison.Ordinal);

    string VariableName(string[] path)
    {
        var parts = path.Where((segment, index) => !(index == 1 && segment == "environment"));
        var name = NotVariableCharacter().Replace(string.Join('_', parts).ToUpperInvariant(), "_");
        if (name.Length == 0 || char.IsDigit(name[0]))
            name = "_" + name;
        var unique = name;
        for (var suffix = 2; variables.Any(variable => variable.Key == unique); suffix++)
            unique = $"{name}_{suffix}";
        return unique;
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex NotProjectCharacter();

    [GeneratedRegex("[^A-Z0-9_]")]
    private static partial Regex NotVariableCharacter();
}
