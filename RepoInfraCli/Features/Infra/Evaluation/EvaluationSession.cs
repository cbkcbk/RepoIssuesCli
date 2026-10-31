using NuLua;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed class EvaluationSession(EvaluationContext context) : IDisposable
{
    public EvaluationContext Context => context;

    public EvaluationResult Result { get; set; } = new(null, []);

    public LuaValue Exports { get; set; } = LuaValue.Nil;

    public void Dispose()
    {
        foreach (var imported in context.Imports)
            imported.Dispose();
        context.State.Dispose();
    }
}
