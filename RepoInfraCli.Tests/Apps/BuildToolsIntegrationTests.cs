using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RepoInfraCli.Features.Infra.Apps;

namespace RepoInfraCli.Tests.Apps;

[Category("Docker")]
[NonParallelizable]
public class BuildToolsIntegrationTests
{
    const string Repository = "snappy/api";

    IContainer registry = null!;

    string Registry => $"{registry.Hostname}:{registry.GetMappedPublicPort(5000)}";

    [OneTimeSetUp]
    public async Task StartRegistry()
    {
        registry = new ContainerBuilder("registry:2")
            .WithPortBinding(5000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPath("/v2/").ForPort(5000)))
            .Build();
        await registry.StartAsync();
    }

    [OneTimeTearDown]
    public async Task StopRegistry() => await registry.DisposeAsync();

    [Test]
    public async Task TagExistsAsksTheRegistryForTheManifest()
    {
        await PushManifestAsync("sha1");
        var client = new RegistryClient(ProcessRunner.Instance, dockerConfigFile: Path.Combine(Path.GetTempPath(), "no-docker-config.json"));

        var present = await client.TagExistsAsync(Registry, Repository, "sha1", CancellationToken.None);
        var absent = await client.TagExistsAsync(Registry, Repository, "sha2", CancellationToken.None);
        var unreachable = await client.TagExistsAsync("localhost:1", Repository, "sha1", CancellationToken.None);

        Assert.That(present, Is.EqualTo(new TagCheck(true)));
        Assert.That(absent, Is.EqualTo(new TagCheck(false)));
        Assert.That(unreachable.Exists, Is.Null);
        Assert.That(unreachable.Problem, Is.Not.Empty);
    }

    async Task PushManifestAsync(string tag)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Registry}") };
        var config = await PushBlobAsync(http, "{}"u8.ToArray());
        var layer = await PushBlobAsync(http, [1, 2, 3]);
        var manifest = $$"""
            {"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json",
             "config":{"mediaType":"application/vnd.oci.image.config.v1+json","digest":"{{config.Digest}}","size":{{config.Size}}},
             "layers":[{"mediaType":"application/vnd.oci.image.layer.v1.tar+gzip","digest":"{{layer.Digest}}","size":{{layer.Size}}}]}
            """;
        using var content = new StringContent(manifest, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.oci.image.manifest.v1+json");
        var response = await http.PutAsync($"/v2/{Repository}/manifests/{tag}", content);
        Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());
    }

    static async Task<(string Digest, int Size)> PushBlobAsync(HttpClient http, byte[] blob)
    {
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(blob));
        var start = await http.PostAsync($"/v2/{Repository}/blobs/uploads/", null);
        var location = start.Headers.Location!.ToString();
        var upload = await http.PutAsync($"{location}{(location.Contains('?') ? '&' : '?')}digest={Uri.EscapeDataString(digest)}", new ByteArrayContent(blob));
        Assert.That(upload.IsSuccessStatusCode, Is.True, await upload.Content.ReadAsStringAsync());
        return (digest, blob.Length);
    }
}
