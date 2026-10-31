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
        var scope = ProviderScope.Register(context, ProviderKind, options, ProviderOptions, context.Here());
        var label = Label(context, scope.Provider);

        return context.Map(
        [
            KeyValuePair.Create("host", context.Named("host", (name, location) => Host(context, scope.Provider, label, name, location))),
            KeyValuePair.Create("role", context.Named("role", (name, _) => Role(context, label, name))),
            KeyValuePair.Create("network", context.Named("network", (name, location) =>
                ComposeModule.Network(context, name, location, NodeId.Of("compose.network", $"{label}/{name}"), external: true))),
            KeyValuePair.Create("database", context.Named("database", (name, location) => Database(context, label, name, location))),
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

    static LuaValue Host(EvaluationContext context, ProviderNode provider, string label, string name, SourceLocation? location)
    {
        var id = NodeId.Of(NodeKinds.Host, $"{label}/{name}");
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
        var id = NodeId.Of("repo.database", $"{label}/{name}");
        context.Recorder.AddComponent(new ComponentNode(id, ProviderKind, NodeKinds.Database, name, MapValue.Empty, true, location));
        return DatabaseValues.Table(context, id, name, context.Output(id, "host"));
    }
}