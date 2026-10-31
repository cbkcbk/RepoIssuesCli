using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Repos;

namespace RepoInfraCli.Features.Infra.Hooks;

/// <summary>Turns a Claude Code hook's input into its JSON output; <c>null</c> means print nothing.</summary>
public static class InfraHooks
{
    public const int MaxDiagnostics = 10;

    public static JsonObject? Handle(InfraHookEvent hookEvent, HookInput? input, string? explicitRoot) => hookEvent switch
    {
        InfraHookEvent.Shell => Shell(input),
        InfraHookEvent.Edit => input is null ? null : Edit(input, explicitRoot, RepoCache.DefaultOffline),
        _ => null,
    };

    public static JsonObject Refuse(string reason) => Permission(new GuardVerdict(GuardDecision.Deny, reason));

    static JsonObject? Shell(HookInput? input)
    {
        if (input is null)
            return Refuse("The infra plugin's guard could not read the hook input, so it can't tell whether this command changes live infrastructure.");
        if (input.Command is not { } command)
            return null;

        var verdict = ShellGuard.Decide(command, input.PermissionMode);
        return verdict.Decision == GuardDecision.Allow ? null : Permission(verdict);
    }

    static JsonObject Permission(GuardVerdict verdict) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = verdict.Decision == GuardDecision.Deny ? "deny" : "ask",
            ["permissionDecisionReason"] = verdict.Reason,
        },
    };

    public static JsonObject? Edit(HookInput input, string? explicitRoot, RepoCache repos)
    {
        if (input.FilePath is not { } filePath)
            return null;

        var file = Path.GetFullPath(filePath, input.Cwd ?? Environment.CurrentDirectory);
        var workspace = new InfraWorkspace(RepoLocator.FindRoot(explicitRoot, Path.GetDirectoryName(file)!));
        if (!workspace.IsInitialized || !file.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) || !InfraWorkspace.IsInside(workspace.Folder, file))
            return null;

        var problems = Problems(workspace, repos);
        var errors = problems.Where(problem => problem.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
            return new JsonObject { ["decision"] = "block", ["reason"] = Report("infra validate --offline found errors after this edit; fix them before going on:", errors) };

        var warnings = problems.Where(problem => problem.Code != DiagnosticCodes.RepoUnavailable).ToList();
        return warnings.Count == 0
            ? null
            : new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PostToolUse",
                    ["additionalContext"] = Report("infra validate --offline warnings after this edit:", warnings),
                },
            };
    }

    static List<InfraDiagnostic> Problems(InfraWorkspace workspace, RepoCache repos)
    {
        var envs = EnvRules.Available(workspace);
        if (envs.Count == 0)
            return [.. InfraEvaluator.Evaluate(workspace, new EvaluationOptions { Repos = repos }).Diagnostics];

        return [.. envs
            .SelectMany(env => InfraEvaluator.Evaluate(workspace, new EvaluationOptions { Env = env, Repos = repos }).Diagnostics
                .Select(diagnostic => diagnostic with { Message = $"[{env}] {diagnostic.Message}" }))
            .DistinctBy(diagnostic => diagnostic.ToString())];
    }

    static string Report(string heading, IReadOnlyList<InfraDiagnostic> diagnostics)
    {
        var lines = new List<string> { heading };
        lines.AddRange(diagnostics.Take(MaxDiagnostics).Select(diagnostic => $"- {diagnostic.Location?.ToString() ?? "(no location)"} {diagnostic.Code} {diagnostic.Message}"));
        if (diagnostics.Count > MaxDiagnostics)
            lines.Add($"- and {diagnostics.Count - MaxDiagnostics} more: run `infra validate --json`.");
        return string.Join('\n', lines);
    }
}
