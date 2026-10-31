using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Secrets;

public interface ISecretResolver
{
    SecretResolution Resolve(InfraWorkspace workspace, string? env, IEnumerable<SecretRef> secrets);
}

public sealed record SecretResolution(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Missing)
{
    public IReadOnlyList<string> Undecryptable { get; init; } = [];

    public bool IsComplete => Missing.Count == 0 && Undecryptable.Count == 0;
}
