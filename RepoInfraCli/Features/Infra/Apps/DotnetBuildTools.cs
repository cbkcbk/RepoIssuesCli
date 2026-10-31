using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Features.Infra.Apps;

public sealed class DotnetBuildTools(IProcessRunner runner, RegistryClient registryClient) : IBuildTools
{
    const int DirtyHashLength = 12;
    static readonly string[] PropertyNames = ["ContainerRegistry", "ContainerRepository", "ContainerBaseImage", "ContainerFamily"];

    public static DotnetBuildTools Default { get; } =
        new(ProcessRunner.Instance, new RegistryClient(ProcessRunner.Instance, new TrafficHandler(new HttpClientHandler(), "registry")));

    public async Task<ContainerProperties> ReadPropertiesAsync(string project, CancellationToken cancellationToken)
    {
        var result = await RunAsync("dotnet", ["msbuild", project, "-nologo", .. PropertyNames.Select(name => $"-getProperty:{name}")], Path.GetDirectoryName(project), cancellationToken);
        if (!result.Succeeded)
            throw new BuildToolException($"dotnet msbuild -getProperty failed for {project}: {result.Tail()}");
        return Parse(result.Stdout);
    }

    public static ContainerProperties Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var properties = document.RootElement.GetProperty("Properties");
            string? Get(string name) => properties.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text.Trim() : null;
            return new ContainerProperties(Get("ContainerRegistry"), Get("ContainerRepository"), Get("ContainerBaseImage"), Get("ContainerFamily"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new BuildToolException($"unexpected output from dotnet msbuild -getProperty: {json.Trim()}");
        }
    }

    public async Task<string?> DirtyHashAsync(string root, CancellationToken cancellationToken)
    {
        if ((await GitAsync(root, ["status", "--porcelain"], cancellationToken)).Trim().Length == 0)
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(await GitAsync(root, ["diff", "HEAD", "--binary"], cancellationToken)));
        var untracked = (await GitAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal);
        foreach (var path in untracked)
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\0{path}\0"));
            hash.AppendData(await File.ReadAllBytesAsync(Path.Combine(root, path), cancellationToken));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..DirtyHashLength];
    }

    async Task<string> GitAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync("git", ["-C", root, .. arguments], root, cancellationToken);
        if (!result.Succeeded)
            throw new BuildToolException($"git {arguments[0]} failed in {root}: {result.Tail()}");
        return result.Stdout;
    }

    public Task<TagCheck> TagExistsAsync(string registry, string repository, string tag, CancellationToken cancellationToken) =>
        registryClient.TagExistsAsync(registry, repository, tag, cancellationToken);

    public async Task PublishAsync(ContainerPublish publish, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunAsync("dotnet", PublishArguments(publish), workingDirectory, cancellationToken);
        if (!result.Succeeded)
            throw new BuildToolException($"dotnet publish -t:PublishContainer failed for {publish.Project}: {result.Tail()}");
    }

    public static IReadOnlyList<string> PublishArguments(ContainerPublish publish) =>
    [
        "publish", publish.Project,
        "-t:PublishContainer",
        "-c", publish.Configuration,
        "-r", publish.Runtime,
        "-nologo",
        $"-p:ContainerRegistry={publish.Registry}",
        $"-p:ContainerRepository={publish.Repository}",
        $"-p:ContainerImageTags={publish.Tag}",
    ];

    async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(fileName, arguments, workingDirectory, null, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw new BuildToolException($"could not run {fileName}: {exception.Message}");
        }
    }
}