using System.Text;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed class EnvironmentSecretResolver(Func<string, string?>? variables = null) : ISecretResolver
{
    readonly Func<string, string?> lookup = variables ?? Environment.GetEnvironmentVariable;

    public SecretResolution Resolve(InfraWorkspace workspace, string? env, IEnumerable<SecretRef> secrets)
    {
        var values = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var path in secrets.Select(secret => secret.Path).Distinct())
        {
            if (TryResolve(env, path) is { } value)
                values[path] = value;
            else
                missing.Add(path);
        }

        return new SecretResolution(values, missing);
    }

    public string? TryResolve(string? env, string path) =>
        (env is null ? null : lookup(VariableName($"{env}/{path}"))) ?? lookup(VariableName(path));

    public static string VariableName(string path)
    {
        var name = new StringBuilder("INFRA_SECRET_");
        foreach (var character in path)
            name.Append(char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        return name.ToString();
    }
}