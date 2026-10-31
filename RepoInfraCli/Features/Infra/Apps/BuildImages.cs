using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed class BuildImages(ILiveValues inner, IReadOnlyList<BuildPlan> builds) : ILiveValues
{
    public const string ImageField = "image";

    readonly Dictionary<NodeId, string> images = builds
        .Where(build => build.Image is not null)
        .ToDictionary(build => build.Build.Id, build => build.Image!);

    public InfraValue? Resolve(OutputRef output) =>
        output.Field == ImageField && images.TryGetValue(output.Node, out var image) ? new StringValue(image) : inner.Resolve(output);

    public bool CreatedThisRun(NodeId resource) => inner.CreatedThisRun(resource);
}
