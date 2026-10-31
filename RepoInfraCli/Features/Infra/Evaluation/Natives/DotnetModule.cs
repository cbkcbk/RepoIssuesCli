using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class DotnetModule : INativeModule
{
    public const string ContainerKind = "dotnet.container";

    static readonly ObjectShape ContainerOptions = Object("ContainerOptions",
        Required("project", Shapes.String),
        Required("registry", Shapes.String),
        Required("tag", Shapes.String),
        Optional("configuration", Shapes.String),
        Optional("runtime", Shapes.String));

    public string Name => "dotnet";

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("container", context.Function(args => Container(context, EvaluationContext.Argument(args, 0)))),
    ]);

    static LuaValue Container(EvaluationContext context, LuaValue options)
    {
        var location = context.Here();
        context.CheckOptions("dotnet.container", options, ContainerOptions, location);
        var map = context.Converter.ConvertMap(options);
        var project = Path.GetFileNameWithoutExtension(map.GetString("project") ?? "") is { Length: > 0 } name ? name : "container";
        var key = context.Recorder.UniqueSuffix(project, candidate => context.Recorder.Builds.Any(build => build.Id == NodeId.Of(ContainerKind, candidate)));
        var id = NodeId.Of(ContainerKind, key);
        context.Recorder.Builds.Add(new BuildNode(id, ContainerKind, map, location));
        return context.Node(id, NodeKinds.Build, tags: [KeyValuePair.Create(BuildTag, EvaluationContext.Text(id.Value))]);
    }
}
