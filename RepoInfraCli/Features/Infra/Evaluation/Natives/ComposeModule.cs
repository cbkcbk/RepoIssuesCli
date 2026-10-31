using NuLua;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class ComposeModule : INativeModule
{
    public string Name => "compose";

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("network", context.Named("compose.network", (name, location) => Network(context, name, location))),
    ]);

    public static LuaValue Network(EvaluationContext context, string name, SourceLocation? location, NodeId? id = null, bool external = false)
    {
        var node = id ?? NodeId.Of("compose.network", name);
        context.Recorder.AddComponent(new ComponentNode(node, "compose", NodeKinds.Network, name, MapValue.Empty, external, location));
        return context.Node(node, NodeKinds.Network, [KeyValuePair.Create("name", EvaluationContext.Text(name))]);
    }
}
