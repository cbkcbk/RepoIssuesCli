using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class RepoModule : INativeModule
{
    public const string ProviderKind = "repo";
    public const string LabelOption = "label";

    static readonly ObjectShape ProviderOptions = Object("ProviderOptions",
        Required("url", Shapes.String),
        Required("ref", Shapes.String),
        Optional("path", Shapes.String));

    public string Name => ProviderKind;

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("provider", context.Function(args => Provider(context, EvaluationContext.Argument(args, 0)))),
    ]);

    static LuaValue Provider(EvaluationContext context, LuaValue options)
    {
        var location = context.Here();
        var scope = ProviderScope.Register(context, ProviderKind, options, ProviderOptions, location);
        var label = Label(context, scope.Provider);
        var imported = ImportedRepo.Open(context, scope.Provider, label, location);

        return context.Map(
        [
            KeyValuePair.Create("host", context.Named("host", (name, at) => imported?.Host(name, at) ?? Host(context, scope.Provider, label, name, at))),
            KeyValuePair.Create("role", context.Named("role", (name, at) =>
            {
                imported?.CheckRole(name, at);
                return Role(context, label, name);
            })),
            KeyValuePair.Create("network", context.Named("network", (name, at) =>
                imported?.Network(name, at) ?? ComposeModule.Network(context, name, at, Placeholder(label, "compose.network", name), external: true))),
            KeyValuePair.Create("database", context.Named("database", (name, at) => imported?.Database(name, at) ?? Database(context, label, name, at))),
        ]);
    }

    static string Label(EvaluationContext context, ProviderNode provider)
    {
        var url = provider.Options.GetString("url") ?? "";
        var last = url.TrimEnd('/').Split('/', ':').LastOrDefault() ?? "";
        var label = last.EndsWith(".git", StringComparison.Ordinal) ? last[..^4] : last;
        label = label.Length == 0 ? provider.Id.Value : label;
        var taken = context.Recorder.Providers
            .Where(other => other.Kind == ProviderKind && other.Id != provider.Id)
            .Select(other => other.Options.GetString(LabelOption))
            .ToHashSet();
        label = context.Recorder.UniqueSuffix(label, taken.Contains);

        var index = context.Recorder.Providers.IndexOf(provider);
        context.Recorder.Providers[index] = provider with
        {
            Options = new MapValue(provider.Options.Entries.SetItem(LabelOption, new StringValue(label))),
        };
        return label;
    }

    static NodeId Placeholder(string label, string kind, string name) => new($"{label}/{NodeId.Of(kind, name)}");

    static LuaValue Host(EvaluationContext context, ProviderNode provider, string label, string name, SourceLocation? location)
    {
        var id = Placeholder(label, NodeKinds.Host, name);
        if (context.Recorder.Hosts.All(host => host.Id != id))
            context.Recorder.Hosts.Add(new HostNode(id, name, new OutputRef(id, "address"), "", null, false, null, null, true, provider.Id, location));
        return HostTasks.For(context).Table(id, name);
    }

    static LuaValue Role(EvaluationContext context, string label, string name)
    {
        var reference = $"{label}/{name}";
        return context.Node(NodeId.Of(NodeKinds.Role, reference), NodeKinds.Role,
            [KeyValuePair.Create("name", EvaluationContext.Text(name))],
            [KeyValuePair.Create(RoleTag, EvaluationContext.Text(reference))]);
    }

    static LuaValue Database(EvaluationContext context, string label, string name, SourceLocation? location)
    {
        var id = Placeholder(label, "repo.database", name);
        context.Recorder.AddComponent(new ComponentNode(id, ProviderKind, NodeKinds.Database, name, MapValue.Empty, true, location));
        return DatabaseValues.Table(context, id, name, context.Output(id, "host"));
    }
}
