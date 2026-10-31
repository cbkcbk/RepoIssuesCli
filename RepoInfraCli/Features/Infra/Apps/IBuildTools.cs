namespace RepoInfraCli.Features.Infra.Apps;

public sealed record ContainerProperties(string? Registry, string? Repository, string? BaseImage, string? Family)
{
    public static ContainerProperties None { get; } = new(null, null, null, null);
}

public sealed record ContainerPublish(string Project, string Configuration, string Runtime, string Registry, string Repository, string Tag)
{
    public string Image => $"{Registry}/{Repository}:{Tag}";
}

public sealed record TagCheck(bool? Exists, string? Problem = null);

public interface IBuildTools
{
    Task<ContainerProperties> ReadPropertiesAsync(string project, CancellationToken cancellationToken);

    Task<bool> IsDirtyAsync(string root, CancellationToken cancellationToken);

    Task<TagCheck> TagExistsAsync(string registry, string repository, string tag, CancellationToken cancellationToken);

    Task PublishAsync(ContainerPublish publish, string workingDirectory, CancellationToken cancellationToken);
}

public sealed class BuildToolException(string message) : Exception(message);
