using System.Collections.Concurrent;
using RepoInfraCli.Features.Infra.Apps;

namespace RepoInfraCli.Tests.Apps;

public sealed class FakeBuildTools : IBuildTools
{
    public ContainerProperties Properties { get; init; } = ContainerProperties.None;

    public string? DirtyHash { get; init; }

    public HashSet<string> ExistingImages { get; init; } = [];

    public string? PublishError { get; init; }

    public Action<ContainerPublish>? OnPublish { get; init; }

    public ConcurrentQueue<ContainerPublish> Published { get; } = new();

    public ConcurrentQueue<string> PropertyReads { get; } = new();

    public Task<ContainerProperties> ReadPropertiesAsync(string project, CancellationToken cancellationToken)
    {
        PropertyReads.Enqueue(project);
        return Task.FromResult(Properties);
    }

    public Task<string?> DirtyHashAsync(string root, CancellationToken cancellationToken) => Task.FromResult(DirtyHash);

    public Task<TagCheck> TagExistsAsync(string registry, string repository, string tag, CancellationToken cancellationToken) =>
        Task.FromResult(new TagCheck(ExistingImages.Contains($"{registry}/{repository}:{tag}")));

    public Task PublishAsync(ContainerPublish publish, string workingDirectory, CancellationToken cancellationToken)
    {
        if (PublishError is not null)
            throw new BuildToolException(PublishError);
        OnPublish?.Invoke(publish);
        Published.Enqueue(publish);
        return Task.CompletedTask;
    }
}