using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Secrets;

public static class SecretCodes
{
    public const string Missing = "secret-missing";
    public const string Undecryptable = "secret-undecryptable";
}

public static class SecretProblems
{
    public static IReadOnlyList<InfraDiagnostic> Diagnostics(SecretResolution resolution, string? env) =>
    [
        .. resolution.Missing.Select(path => InfraDiagnostic.Error(SecretCodes.Missing, MissingMessage(env, path))),
        .. resolution.Undecryptable.Select(path => InfraDiagnostic.Error(SecretCodes.Undecryptable, UndecryptableMessage(env, path))),
    ];

    public static string SetCommand(string? env, string path) =>
        env is null ? $"infra secrets set {path}" : $"infra secrets set {env}/{path}";

    public static string MissingMessage(string? env, string path)
    {
        var shared = env is null ? "" : $" (or '{SetCommand(null, path)}' for every env)";
        return $"secret '{path}' is not set; run '{SetCommand(env, path)}'{shared}, or set {EnvironmentSecretResolver.VariableName(env is null ? path : $"{env}/{path}")}";
    }

    public static string UndecryptableMessage(string? env, string path) =>
        $"secret '{path}' is in {RepoLocator.InfraFolderName}/{SecretStore.FileName} but this machine cannot decrypt it ({string.Join(" or ", ChainedSecretResolver.Candidates(env, path))}); " +
        "send 'infra secrets key' to a recipient and have them run 'infra secrets recipients add <name> <key>', " +
        $"or set {EnvironmentSecretResolver.VariableName(path)}";
}

public static class SecretResolverExtensions
{
    public static SecretResolution ResolveAndRedact(this ISecretResolver resolver, InfraWorkspace workspace, string? env, IEnumerable<SecretRef> secrets, ISecretRedactor redactor)
    {
        var resolution = resolver.Resolve(workspace, env, secrets);
        foreach (var value in resolution.Values.Values)
            redactor.AddSecret(value);
        return resolution;
    }
}
