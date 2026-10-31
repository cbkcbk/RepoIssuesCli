using System.Security.Cryptography;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed class ChainedSecretResolver(SecretEnvironment machine) : ISecretResolver
{
    readonly EnvironmentSecretResolver overrides = new(machine.Variables);

    public SecretResolution Resolve(InfraWorkspace workspace, string? env, IEnumerable<SecretRef> secrets)
    {
        var values = new Dictionary<string, string>();
        var missing = new List<string>();
        var undecryptable = new List<string>();
        SecretStore? store = null;
        Lazy<SecretKey?> key = new(machine.LoadKey);
        try
        {
            foreach (var path in secrets.Select(secret => secret.Path).Distinct())
            {
                if (overrides.TryResolve(env, path) is { } overridden)
                {
                    values[path] = overridden;
                    continue;
                }

                store ??= SecretStore.Load(workspace);
                if (StoredName(store, env, path) is not { } stored)
                    missing.Add(path);
                else if (Decrypt(store, stored, key.Value) is { } value)
                    values[path] = value;
                else
                    undecryptable.Add(path);
            }
        }
        finally
        {
            if (key.IsValueCreated)
                key.Value?.Dispose();
        }

        return new SecretResolution(values, missing) { Undecryptable = undecryptable };
    }

    public static IEnumerable<string> Candidates(string? env, string path) => env is null ? [path] : [$"{env}/{path}", path];

    static string? StoredName(SecretStore store, string? env, string path) =>
        Candidates(env, path).FirstOrDefault(store.Secrets.ContainsKey);

    static string? Decrypt(SecretStore store, string name, SecretKey? key)
    {
        if (key is null)
            return null;
        try
        {
            return SecretCipher.Decrypt(name, store.Secrets[name], key);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
