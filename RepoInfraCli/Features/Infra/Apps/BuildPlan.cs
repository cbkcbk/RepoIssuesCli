using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts.Tasks;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed record BuildPlan(
    BuildNode Build,
    string Label,
    IReadOnlyList<string> Apps,
    string? Image,
    IReadOnlyList<Change> Changes,
    string? Note = null,
    string? Error = null,
    ContainerPublish? Publish = null)
{
    public bool NeedsBuild => Error is null && Publish is not null && Changes.Count > 0;
}
