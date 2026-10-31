using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation;

public static class EnvRules
{
    public static IReadOnlyList<string> Available(InfraWorkspace workspace) =>
        Directory.Exists(workspace.EnvsFolder)
            ? [.. Directory.EnumerateFileSystemEntries(workspace.EnvsFolder)
                .Select(entry => Directory.Exists(entry) ? Path.GetFileName(entry) : Path.GetFileNameWithoutExtension(entry))
                .Where(name => !name.StartsWith('.'))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)]
            : [];

    public static InfraDiagnostic? Check(InfraWorkspace workspace, string? env)
    {
        var envsPath = $"{RepoLocator.InfraFolderName}/{InfraWorkspace.EnvsFolderName}/";
        if (!Directory.Exists(workspace.EnvsFolder))
            return env is null
                ? null
                : InfraDiagnostic.Error(DiagnosticCodes.UnknownEnv, $"--env {env} was given but {envsPath} does not exist; drop --env or create {envsPath}{env}/");

        var available = Available(workspace);
        if (env is null)
            return InfraDiagnostic.Error(DiagnosticCodes.EnvRequired, $"--env is required because {envsPath} exists (one of: {string.Join(", ", available)})");

        return available.Contains(env, StringComparer.Ordinal)
            ? null
            : InfraDiagnostic.Error(DiagnosticCodes.UnknownEnv, $"unknown env '{env}'; {envsPath} has: {(available.Count == 0 ? "nothing" : string.Join(", ", available))}");
    }
}
