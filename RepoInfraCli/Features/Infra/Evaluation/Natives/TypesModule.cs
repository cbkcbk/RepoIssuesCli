using NuLua;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class TypesModule : INativeModule
{
    public string Name => "types";

    public LuaValue Create(EvaluationContext context) => LuaValue.FromTable(context.Table());
}
