using NuLua;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class ProviderScope(EvaluationContext context, ProviderNode provider)
{
    public ProviderNode Provider => provider;

    public static ProviderScope Register(EvaluationContext context, string kind, LuaValue options, ObjectShape shape, SourceLocation? location)
    {
        context.CheckOptions($"{kind}.provider", options, shape, location);
        var node = new ProviderNode(context.Recorder.NextProviderId(kind), kind, context.Converter.ConvertMap(options), location);
        context.Recorder.Providers.Add(node);
        return new ProviderScope(context, node);
    }

    public ResourceNode Add(
        string kind,
        string name,
        LuaValue options,
        ObjectShape? shape,
        SourceLocation? location,
        NodeId? parent = null,
        bool existing = false,
        string? key = null)
    {
        if (shape is not null)
            context.CheckOptions($"{kind} \"{name}\"", options, shape, location);

        var map = context.Converter.ConvertMap(options);
        var protect = map.GetBool("protect");
        map = map.Without("protect");
        var id = new NodeId($"{provider.Id}.{kind}:{key ?? name}");
        var dependsOn = map.References()
            .Select(reference => (NodeId?)reference)
            .Append(parent)
            .OfType<NodeId>()
            .Where(reference => reference != id)
            .Distinct()
            .ToList();

        var node = new ResourceNode(id, provider.Kind, provider.Id, kind, name, parent, map, protect, false, existing, location, dependsOn);
        context.Recorder.Resources.Add(node);
        return node;
    }
}

public static class DatabaseValues
{
    public static LuaValue Table(EvaluationContext context, NodeId id, string name, LuaValue host) =>
        context.Node(id, NodeKinds.Database,
        [
            KeyValuePair.Create("name", EvaluationContext.Text(name)),
            KeyValuePair.Create("host", host),
            KeyValuePair.Create("connection_string", context.Output(id, "connection_string", sensitive: true)),
        ]);
}
