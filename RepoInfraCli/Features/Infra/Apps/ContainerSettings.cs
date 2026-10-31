using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed record ContainerTarget(string Registry, string Repository, string? BaseImage, string? Family);

public sealed record ContainerResolution(ContainerTarget? Target, IReadOnlyList<string> Conflicts);

public static class ContainerSettings
{
    public const string DefaultConfiguration = "Release";
    public const string DefaultRuntime = "linux-x64";

    public static string ProjectPath(InfraWorkspace workspace, BuildNode build) =>
        Path.GetFullPath(Path.Combine(workspace.Root, build.Options.GetString("project") ?? ""));

    public static ContainerResolution Resolve(BuildNode build, ContainerProperties properties)
    {
        var project = build.Options.GetString("project") ?? "";
        var (host, prefix) = SplitRegistry(build.Options.GetString("registry"));
        var conflicts = new List<string>();

        if (host is not null && properties.Registry is { } msbuildRegistry && !string.Equals(host, msbuildRegistry, StringComparison.OrdinalIgnoreCase))
            conflicts.Add($"registry \"{host}\" in infra.luau conflicts with ContainerRegistry \"{msbuildRegistry}\" in {project}");
        var registry = host ?? properties.Registry;
        if (registry is null)
            conflicts.Add($"no registry: set registry in dotnet.container or ContainerRegistry in {project}");

        var repository = Repository(project, prefix, properties.Repository, conflicts);
        return new ContainerResolution(
            conflicts.Count == 0 ? new ContainerTarget(registry!, repository, properties.BaseImage, properties.Family) : null,
            conflicts);
    }

    static string Repository(string project, string? prefix, string? msbuildRepository, List<string> conflicts)
    {
        var name = Path.GetFileNameWithoutExtension(project).ToLowerInvariant();
        if (msbuildRepository is null)
            return prefix is null ? name : $"{prefix}/{name}";
        if (prefix is null || msbuildRepository.StartsWith(prefix + "/", StringComparison.Ordinal))
            return msbuildRepository;
        if (msbuildRepository.Contains('/'))
            conflicts.Add($"registry path \"{prefix}\" in infra.luau conflicts with ContainerRepository \"{msbuildRepository}\" in {project}");
        return $"{prefix}/{msbuildRepository}";
    }

    public static (string? Host, string? Prefix) SplitRegistry(string? registry)
    {
        if (string.IsNullOrWhiteSpace(registry))
            return (null, null);
        var trimmed = registry.Trim().TrimEnd('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? (trimmed, null) : (trimmed[..slash], trimmed[(slash + 1)..]);
    }
}
