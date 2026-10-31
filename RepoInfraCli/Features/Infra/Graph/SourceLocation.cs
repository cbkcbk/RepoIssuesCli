namespace RepoInfraCli.Features.Infra.Graph;

public sealed record SourceLocation(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}
