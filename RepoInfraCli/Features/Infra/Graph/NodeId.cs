namespace RepoInfraCli.Features.Infra.Graph;

public readonly record struct NodeId(string Value)
{
    public override string ToString() => Value;

    public static NodeId Of(string kind, string name) => new($"{kind}:{name}");
}
