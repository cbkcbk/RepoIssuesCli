using System.Collections.Concurrent;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed class SecretLookup(ISecretResolver resolver, InfraWorkspace workspace, string? env, ISecretRedactor redactor)
{
    readonly ConcurrentDictionary<string, string> known = new(StringComparer.Ordinal);

    public static SecretLookup Unavailable(InfraWorkspace workspace) =>
        new(new EnvironmentSecretResolver(_ => null), workspace, null, new SecretRedactor());

    public string Get(SecretRef secret)
    {
        if (known.TryGetValue(secret.Path, out var cached))
            return cached;

        var resolution = resolver.ResolveAndRedact(workspace, env, [secret], redactor);
        if (!resolution.Values.TryGetValue(secret.Path, out var value))
            throw new SecretUnavailableException(SecretProblems.Diagnostics(resolution, env)[0].Message);
        return known.GetOrAdd(secret.Path, value);
    }
}

public sealed class SecretUnavailableException(string message) : Exception(message);
