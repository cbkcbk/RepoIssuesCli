using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Validation;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Evaluate .repo-infra/infra.luau without touching anything and report problems: unknown or missing options, duplicates, protect on name-prefix kinds, files outside the repo, undefined hosts. Exits with 1 on errors.")]
public sealed class ValidateCommand(IAnsiConsole console, ErrorConsole errors, IBuildTools builds) : InfraCommand<InfraSettings>(console, errors)
{
    protected override int Run(InfraWorkspace workspace, InfraSettings settings, InfraLog log)
    {
        RequireInitialized(workspace);
        if (EnvRules.Check(workspace, settings.Env) is { Code: DiagnosticCodes.EnvRequired } missing)
            throw new UsageException(missing.Message + ".");

        var result = InfraEvaluator.Evaluate(workspace, new EvaluationOptions { Env = settings.Env, Log = log });
        if (result.Graph is { } evaluated)
            result = result with { Diagnostics = [.. result.Diagnostics, .. BuildValidator.ValidateAsync(evaluated, workspace, builds).GetAwaiter().GetResult()] };
        foreach (var diagnostic in result.Diagnostics)
            log.Record(diagnostic.Severity == Severity.Error ? InfraLogLevel.Error : InfraLogLevel.Warning, "validate.diagnostic", diagnostic.ToString(),
                new Dictionary<string, object?> { ["code"] = diagnostic.Code, ["file"] = diagnostic.Location?.File, ["line"] = diagnostic.Location?.Line });

        var summary = result.Graph is { } graph ? GraphSummary.From(graph) : null;
        if (settings.Json)
        {
            CliOutput.WriteJson(Console, InfraJson.SerializeOutput(new ValidateResult(
                !result.HasErrors,
                [.. result.Errors.Select(DiagnosticView.From)],
                [.. result.Warnings.Select(DiagnosticView.From)],
                summary)));
        }
        else
        {
            if (log.Verbosity > 0 && result.Graph is not null)
                GraphOutline.Write(Console, result.Graph);
            ProblemTable.Write(Console, [.. result.Diagnostics.Select(diagnostic =>
                new ProblemRow(diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Location?.ToString()))]);
        }

        return result.HasErrors ? InfraExitCodes.Invalid : InfraExitCodes.Success;
    }
}
