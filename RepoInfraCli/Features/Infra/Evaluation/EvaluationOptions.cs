using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Evaluation;

public enum EvaluationMode
{
    Full,
    Reference,
}

public sealed record EvaluationOptions
{
    public string? Env { get; init; }

    public EvaluationMode Mode { get; init; } = EvaluationMode.Full;

    public InfraLog? Log { get; init; }
}

public sealed record EvaluationResult(InfraGraph? Graph, IReadOnlyList<InfraDiagnostic> Diagnostics)
{
    public IEnumerable<InfraDiagnostic> Errors => Diagnostics.Where(diagnostic => diagnostic.Severity == Severity.Error);

    public IEnumerable<InfraDiagnostic> Warnings => Diagnostics.Where(diagnostic => diagnostic.Severity == Severity.Warning);

    public bool HasErrors => Errors.Any();
}

public sealed class InfraScriptException(string code, string message, SourceLocation? location) : Exception(message)
{
    public string Code => code;

    public SourceLocation? Location => location;

    public InfraDiagnostic ToDiagnostic() => InfraDiagnostic.Error(code, Message, location);
}
