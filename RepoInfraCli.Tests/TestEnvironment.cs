using RepoInfraCli.Features.Infra.Repos;

namespace RepoInfraCli.Tests;

[SetUpFixture]
public sealed class TestEnvironment
{
    public const string AllowProtocolVariable = "GIT_ALLOW_PROTOCOL";

    string cache = "";

    [OneTimeSetUp]
    public void SetUp()
    {
        cache = Directory.CreateTempSubdirectory("infra-repo-cache-").FullName;
        Environment.SetEnvironmentVariable(RepoCache.RootVariable, cache);
        Environment.SetEnvironmentVariable(AllowProtocolVariable, "file");
    }

    [OneTimeTearDown]
    public void TearDown() => GitRemote.Delete(cache);
}
