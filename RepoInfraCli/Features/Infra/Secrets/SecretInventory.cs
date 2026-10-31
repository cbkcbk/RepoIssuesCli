using System.Security.Cryptography;
using RepoInfraCli.Features.Infra.Evaluation;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed record RecipientEntry(string Name, string PublicKey, bool Self);

public sealed record SecretEntry(string Name, IReadOnlyList<string> Readers, bool Readable, bool? Referenced, string? Problem);

public sealed record MissingSecret(string Path, string? Env, string Command);

public sealed record SecretInventory(
    IReadOnlyList<RecipientEntry> Recipients,
    IReadOnlyList<SecretEntry> Secrets,
    IReadOnlyList<MissingSecret> Missing,
    IReadOnlyList<string> Notes)
{
    public static SecretInventory Build(InfraWorkspace workspace, SecretStore store, SecretKey? key, string? env)
    {
        var notes = new List<string>();
        var references = References(workspace, env, notes);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<MissingSecret>();
        foreach (var (scope, path) in references?.Paths ?? [])
        {
            if (ChainedSecretResolver.Candidates(scope, path).FirstOrDefault(store.Secrets.ContainsKey) is { } stored)
                used.Add(stored);
            else
                missing.Add(new MissingSecret(path, scope, SecretProblems.SetCommand(scope, path)));
        }

        var complete = references is { Complete: true };
        return new SecretInventory(
            [.. store.Recipients.Select(entry => new RecipientEntry(entry.Key, entry.Value, entry.Value == key?.PublicKey))],
            [.. store.Secrets.Keys.Select(name => Entry(store, name, key, complete ? used.Contains(name) : null))],
            missing,
            notes);
    }

    static SecretEntry Entry(SecretStore store, string name, SecretKey? key, bool? referenced)
    {
        try
        {
            var readers = store.Readers(name);
            return new SecretEntry(name, readers, key is not null && SecretCipher.IsReadableBy(store.Secrets[name], key.KeyId), referenced, null);
        }
        catch (CryptographicException exception)
        {
            return new SecretEntry(name, [], false, referenced, exception.Message);
        }
    }

    sealed record ReferenceSet(IReadOnlyList<(string? Env, string Path)> Paths, bool Complete);

    static ReferenceSet? References(InfraWorkspace workspace, string? env, List<string> notes)
    {
        if (!workspace.IsInitialized)
        {
            notes.Add($"No {RepoLocator.InfraFolderName}/{InfraWorkspace.EntryFileName}; referenced secrets were not checked.");
            return null;
        }

        IReadOnlyList<string?> envs = env is not null ? [env] : EnvRules.Available(workspace) is { Count: > 0 } available ? [.. available] : [null];
        var paths = new List<(string?, string)>();
        var complete = true;
        foreach (var scope in envs)
        {
            if (EnvRules.Check(workspace, scope) is { } problem)
            {
                notes.Add(problem.Message);
                complete = false;
                continue;
            }

            var result = InfraEvaluator.Evaluate(workspace, new EvaluationOptions { Env = scope });
            if (result.Graph is null)
            {
                var reason = result.Errors.FirstOrDefault()?.ToString() ?? "unknown error";
                notes.Add($"{RepoLocator.InfraFolderName}/{InfraWorkspace.EntryFileName} did not evaluate{(scope is null ? "" : $" for env {scope}")}: {reason}");
                complete = false;
                continue;
            }
            paths.AddRange(result.Graph.Secrets.Select(secret => (scope, secret.Path)));
        }
        return new ReferenceSet(paths, complete);
    }
}
