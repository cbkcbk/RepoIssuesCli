using RepoInfraCli.Features.Infra.Apps;

namespace RepoInfraCli.Tests.Apps;

public class DotnetBuildToolsTests
{
    [Test]
    public async Task ReadsContainerPropertiesWithMsbuildGetProperty()
    {
        var folder = Directory.CreateTempSubdirectory("infra-msbuild-").FullName;
        try
        {
            var project = Path.Combine(folder, "Api.csproj");
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ContainerRepository>team/api</ContainerRepository>
                    <ContainerFamily>noble-chiseled</ContainerFamily>
                  </PropertyGroup>
                </Project>
                """);

            var properties = await DotnetBuildTools.Default.ReadPropertiesAsync(project, CancellationToken.None);

            Assert.That(properties.Repository, Is.EqualTo("team/api"));
            Assert.That(properties.Family, Is.EqualTo("noble-chiseled"));
            Assert.That(properties.Registry, Is.Null);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void MissingProjectIsABuildToolError() =>
        Assert.ThrowsAsync<BuildToolException>(() => DotnetBuildTools.Default.ReadPropertiesAsync(Path.Combine(Path.GetTempPath(), "missing-infra", "Nope.csproj"), CancellationToken.None));

    [Test]
    public async Task GitStatusTellsWhetherTheTreeIsDirty()
    {
        var folder = Directory.CreateTempSubdirectory("infra-git-").FullName;
        try
        {
            await Git(folder, "init", "-q");
            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "a");
            await Git(folder, "add", ".");
            await Git(folder, "-c", "user.email=t@example.test", "-c", "user.name=t", "commit", "-q", "-m", "init");

            var clean = await DotnetBuildTools.Default.IsDirtyAsync(folder, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "b");
            var dirty = await DotnetBuildTools.Default.IsDirtyAsync(folder, CancellationToken.None);

            Assert.That(clean, Is.False);
            Assert.That(dirty, Is.True);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
    }

    static async Task Git(string folder, params string[] arguments)
    {
        var result = await ProcessRunner.Instance.RunAsync("git", arguments, folder);
        Assert.That(result.Succeeded, Is.True, result.Tail());
    }

    [Test]
    public void PropertiesParseFromMsbuildJson()
    {
        var properties = DotnetBuildTools.Parse("""{ "Properties": { "ContainerRegistry": "ghcr.io", "ContainerRepository": "", "ContainerBaseImage": "mcr.microsoft.com/dotnet/aspnet:10.0" } }""");

        Assert.That(properties, Is.EqualTo(new ContainerProperties("ghcr.io", null, "mcr.microsoft.com/dotnet/aspnet:10.0", null)));
        Assert.Throws<BuildToolException>(() => DotnetBuildTools.Parse("MSB1009: Project file does not exist."));
    }
}
