using System.Text.Json;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Tests.Compose;
using RepoInfraCli.Tests.Hosts;
using Spectre.Console.Cli;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Apps;

public class AppBuildTests
{
    const string Image = "registry.example/snappy/api:sha1";

    const string AppScript = """
                             local dotnet = require("@infra/dotnet")
                             local web = infra.host "web" { address = "10.0.0.1", user = "root" }

                             local api = infra.app "api" {
                               build = dotnet.container { project = "Api/Api.csproj", registry = "registry.example/snappy", tag = "sha1" },
                               deploy = function(h, image)
                                 h:compose "api" { services = { api = { image = image } } }
                               end,
                             }

                             local function base(h)
                               h:shell "echo hi"
                             end

                             infra.play "base" { hosts = { web }, roles = { base } }
                             infra.play "apps" { hosts = { web }, apps = { api } }
                             """;

    TempRepo repo = null!;

    [SetUp]
    public void SetUp()
    {
        repo = new TempRepo();
        repo.WriteScript(AppScript);
        repo.WriteFile("Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    [TearDown]
    public void TearDown() => repo.Dispose();

    static FakeConnector Web() => new FakeConnector().Host("web", host => host.On("docker inspect", "api running none 172.18.0.2\n"));

    RunResult Run(IHostConnector connector, IBuildTools tools, params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, services: services =>
        {
            services.RegisterInstance(connector);
            services.RegisterInstance(tools);
        }).Run([.. args, "--root", repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    [Test]
    public void PlanShowsBuildsBetweenResourcesAndPlaysAndDeploysTheBuiltTag()
    {
        var result = Run(Web(), new FakeBuildTools(), "plan");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        var lines = result.Output.Split('\n').Select(line => line.TrimEnd()).ToList();
        Assert.That(lines, Does.Contain("Builds").And.Contain($"  + dotnet.container Api: build and push {Image}"));
        Assert.That(lines.IndexOf("Resources"), Is.LessThan(lines.IndexOf("Builds")));
        Assert.That(lines.IndexOf("Builds"), Is.LessThan(lines.IndexOf("Plays")));
        Assert.That(result.Output, Does.Contain("+ compose api: create /opt/api/compose.yaml").And.Not.Contain(InfraValue.UnknownDisplay));
    }

    [Test]
    public void ExistingTagSkipsTheBuild()
    {
        var tools = new FakeBuildTools { ExistingImages = [Image] };

        var result = Run(Web(), tools, "apply", "--yes", "--play", "apps");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain($"dotnet.container Api ({Image} is already in the registry)"));
        Assert.That(tools.Published, Is.Empty);
    }

    [Test]
    public void ApplyBuildsBeforePlaysAndPassesTheImageToCompose()
    {
        var connector = Web();
        var hostTouchedBeforeBuild = false;
        var tools = new FakeBuildTools { OnPublish = _ => hostTouchedBeforeBuild = connector.Opened["web"].Uploads.Count > 0 || connector.Opened["web"].Scripts.Any(script => script.Contains("echo hi")) };

        var result = Run(connector, tools, "apply", "--yes");

        Assert.That(result.ExitCode, Is.Zero, result.Output + result.Errors);
        var publish = tools.Published.Single();
        Assert.That(publish, Is.EqualTo(publish with { Registry = "registry.example", Repository = "snappy/api", Tag = "sha1", Configuration = "Release", Runtime = "linux-x64" }));
        Assert.That(publish.Project, Is.EqualTo(Path.Combine(repo.Root, "Api", "Api.csproj")));
        Assert.That(hostTouchedBeforeBuild, Is.False);
        Assert.That(result.Output, Does.Contain("done local api > dotnet.container Api"));
        var compose = connector.Opened["web"].Uploads.Single(upload => upload.Path == "/opt/api/compose.yaml");
        Assert.That(System.Text.Encoding.UTF8.GetString(compose.Content), Does.Contain($"image: \"{Image}\""));
    }

    [Test]
    public void BuildFailureStopsBeforeAnyPlay()
    {
        var connector = Web();

        var result = Run(connector, new FakeBuildTools { PublishError = "CONTAINER1013: push failed" }, "apply", "--yes");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid), result.Output);
        Assert.That(result.Output, Does.Contain("failed local api > dotnet.container Api: CONTAINER1013: push failed"));
        Assert.That(connector.Opened["web"].Scripts, Has.None.Contains("echo hi"));
    }

    [Test]
    public void DirtyTreeNeedsAllowDirtyAndGetsATimestampedTag()
    {
        var tools = new FakeBuildTools { Dirty = true };

        var blocked = Run(Web(), tools, "plan", "--json");
        var allowed = Run(Web(), tools, "plan", "--allow-dirty", "--json");

        Assert.That(blocked.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        var problem = JsonDocument.Parse(blocked.Output).RootElement.GetProperty("problems")[0].GetProperty("message").GetString();
        Assert.That(problem, Does.Contain("uncommitted changes").And.Contain("--allow-dirty"));
        Assert.That(allowed.ExitCode, Is.Zero, allowed.Output);
        var build = JsonDocument.Parse(allowed.Output).RootElement.GetProperty("builds")[0];
        Assert.That(build.GetProperty("image").GetString(), Does.Match(@"^registry\.example/snappy/api:sha1-dirty-\d{14}$"));
        Assert.That(build.GetProperty("apps")[0].GetString(), Is.EqualTo("api"));
    }

    [Test]
    public void BuildsOnlyCoverAppsOfIncludedPlays()
    {
        var tools = new FakeBuildTools();

        var result = Run(Web(), tools, "plan", "--play", "base");

        Assert.That(result.Output, Does.Not.Contain("Builds"));
        Assert.That(tools.PropertyReads, Is.Empty);
    }

    [Test]
    public void ConflictingMsbuildSettingsFailValidateAndPlan()
    {
        var tools = new FakeBuildTools { Properties = new ContainerProperties("ghcr.io", null, null, null) };

        var validate = Run(Web(), tools, "validate", "--json");
        var plan = Run(Web(), tools, "plan");

        Assert.That(validate.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        var error = JsonDocument.Parse(validate.Output).RootElement.GetProperty("errors")[0];
        Assert.That(error.GetProperty("code").GetString(), Is.EqualTo(DiagnosticCodes.ContainerConflict));
        Assert.That(error.GetProperty("message").GetString(), Does.Contain("registry \"registry.example\" in infra.luau conflicts with ContainerRegistry \"ghcr.io\""));
        Assert.That(error.GetProperty("line").GetInt32(), Is.GreaterThan(0));
        Assert.That(plan.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(plan.Output, Does.Contain("error dotnet.container Api: registry \"registry.example\""));
    }

    [Test]
    public void MissingProjectIsAPlanErrorButNotAValidateError()
    {
        File.Delete(Path.Combine(repo.Root, "Api", "Api.csproj"));

        var validate = Run(Web(), new FakeBuildTools(), "validate");
        var plan = Run(Web(), new FakeBuildTools(), "plan");

        Assert.That(validate.ExitCode, Is.Zero, validate.Output);
        Assert.That(plan.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(plan.Output, Does.Contain("project Api/Api.csproj does not exist").And.Contain($"{InfraValue.UnknownDisplay}: api.image"));
    }

    [TestCase("registry.example/snappy", null, null, "registry.example", "snappy/api")]
    [TestCase("registry.example", null, null, "registry.example", "api")]
    [TestCase("registry.example/snappy", "registry.example", "web", "registry.example", "snappy/web")]
    [TestCase("registry.example/snappy", null, "snappy/web", "registry.example", "snappy/web")]
    public void LuauFillsGapsInMsbuildSettings(string registry, string? msbuildRegistry, string? msbuildRepository, string expectedRegistry, string expectedRepository)
    {
        var build = new BuildNode(new NodeId("dotnet.container:Api"), "dotnet.container", Options.Map(("project", Options.Text("src/Api.csproj")), ("registry", Options.Text(registry))), null);

        var resolution = ContainerSettings.Resolve(build, new ContainerProperties(msbuildRegistry, msbuildRepository, "mcr.microsoft.com/dotnet/aspnet:10.0", null));

        Assert.That(resolution.Conflicts, Is.Empty);
        Assert.That(resolution.Target, Is.EqualTo(new ContainerTarget(expectedRegistry, expectedRepository, "mcr.microsoft.com/dotnet/aspnet:10.0", null)));
    }

    [Test]
    public void RepositoryOutsideTheRegistryPathConflicts()
    {
        var build = new BuildNode(new NodeId("dotnet.container:Api"), "dotnet.container", Options.Map(("project", Options.Text("Api.csproj")), ("registry", Options.Text("r.example/team"))), null);

        var resolution = ContainerSettings.Resolve(build, new ContainerProperties(null, "other/api", null, null));

        Assert.That(resolution.Target, Is.Null);
        Assert.That(resolution.Conflicts.Single(), Does.Contain("registry path \"team\"").And.Contain("ContainerRepository \"other/api\""));
    }

    [Test]
    public void BuildImagesAnswersImageRefsAndDelegatesTheRest()
    {
        var build = new BuildNode(new NodeId("dotnet.container:Api"), "dotnet.container", MapValue.Empty, null);
        var inner = new StaticLiveValues(("droplet:web", "ipv4_address", "1.2.3.4"));
        var images = new BuildImages(inner, [new BuildPlan(build, "dotnet.container Api", ["api"], Image, [])]);

        Assert.That(images.Resolve(new OutputRef(build.Id, "image")), Is.EqualTo(new StringValue(Image)));
        Assert.That(images.Resolve(new OutputRef(new NodeId("droplet:web"), "ipv4_address")), Is.EqualTo(new StringValue("1.2.3.4")));
        Assert.That(images.Resolve(new OutputRef(new NodeId("dotnet.container:Other"), "image")), Is.Null);
    }

    [Test]
    public void PublishUsesPublishContainerWithTheResolvedTarget()
    {
        var arguments = DotnetBuildTools.PublishArguments(new ContainerPublish("/repo/Api/Api.csproj", "Release", "linux-x64", "registry.example", "snappy/api", "sha1"));

        Assert.That(arguments, Is.EqualTo(new[]
        {
            "publish", "/repo/Api/Api.csproj", "-t:PublishContainer", "-c", "Release", "-r", "linux-x64", "-nologo",
            "-p:ContainerRegistry=registry.example", "-p:ContainerRepository=snappy/api", "-p:ContainerImageTags=sha1",
        }));
    }
}
