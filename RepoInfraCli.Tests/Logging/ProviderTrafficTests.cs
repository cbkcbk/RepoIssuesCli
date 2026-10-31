using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Logging;

namespace RepoInfraCli.Tests.Logging;

public class ProviderTrafficTests
{
    sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Test]
    public async Task HttpRequestAndResponseAreLoggedWithCredentialsRedacted()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        using var scope = log.UseAsCurrent();
        using var http = new HttpClient(new TrafficHandler(new Answer(_ => Json("""{"database":{"connection":{"user":"doadmin","password":"pw-123","uri":"postgresql://doadmin:pw-123@db"}}}"""))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.digitalocean.com/v2/databases");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "dop_v1_token");
        request.Content = new StringContent("""{"name":"main","size":"db-s-1vcpu-1gb"}""", Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);

        var text = events.ToString();
        var lines = TestLogs.Events(text);
        var sent = lines.Single(line => line.EventName() == "provider.request");
        var received = lines.Single(line => line.EventName() == "provider.response");
        Assert.That(sent.GetProperty("Provider").GetString(), Is.EqualTo("digitalocean"));
        Assert.That(sent.GetProperty("Body").GetString(), Is.EqualTo("""{"name":"main","size":"db-s-1vcpu-1gb"}"""));
        Assert.That(sent.GetProperty("Headers").GetString(), Does.Contain("Authorization: (secret)"));
        Assert.That(received.GetProperty("Status").GetInt32(), Is.EqualTo(200));
        Assert.That(received.GetProperty("Body").GetString(),
            Is.EqualTo("""{"database":{"connection":{"user":"doadmin","password":"(secret)","uri":"(secret)"}}}"""));
        Assert.That(text, Does.Not.Contain("dop_v1_token").And.Not.Contain("pw-123"));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("pw-123"));
    }

    [Test]
    public void FailedRequestsAreLoggedAndRethrown()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        using var scope = log.UseAsCurrent();
        using var http = new HttpClient(new TrafficHandler(new Answer(_ => throw new HttpRequestException("connection refused")), "registry"));

        Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://ghcr.io/v2/"));

        var failed = TestLogs.Events(events.ToString()).Single(line => line.EventName() == "provider.failed");
        Assert.That(failed.GetProperty("Provider").GetString(), Is.EqualTo("registry"));
        Assert.That(failed.GetProperty("@m").GetString(), Does.Contain("connection refused"));
    }

    [Test]
    public async Task BinaryBodiesAreLoggedAsTypeAndLength()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        using var scope = log.UseAsCurrent();
        using var http = new HttpClient(new TrafficHandler(new Answer(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
        })));

        await http.GetAsync("https://bucket.fra1.digitaloceanspaces.com/dump");

        var received = TestLogs.Events(events.ToString()).Single(line => line.EventName() == "provider.response");
        Assert.That(received.GetProperty("Provider").GetString(), Is.EqualTo("spaces"));
        Assert.That(received.GetProperty("Body").GetString(), Is.EqualTo("(application/octet-stream, 3 bytes)"));
    }

    [Test]
    public async Task NothingIsLoggedWithoutACurrentLog()
    {
        var calls = 0;
        using var http = new HttpClient(new TrafficHandler(new Answer(_ =>
        {
            calls++;
            return Json("{}");
        })));

        await http.GetAsync("https://api.cloudflare.com/client/v4/zones");

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(InfraLog.Current, Is.Null);
    }

    [Test]
    public void SignedQueryParametersAreRedacted()
    {
        var url = TrafficRedaction.Url(new Uri("https://b.fra1.digitaloceanspaces.com/k?X-Amz-Credential=abc&prefix=x&X-Amz-Signature=def"));

        Assert.That(url, Is.EqualTo("https://b.fra1.digitaloceanspaces.com/k?X-Amz-Credential=(secret)&prefix=x&X-Amz-Signature=(secret)"));
    }

    [Test]
    public void LongBodiesAreCutWithTheirFullLength()
    {
        var body = TrafficRedaction.Body(new string('a', TrafficRedaction.MaxBodyLength + 10), new SecretRedactor());

        Assert.That(body, Does.StartWith(new string('a', 100)).And.EndWith($"... (truncated, {TrafficRedaction.MaxBodyLength + 10} bytes)"));
    }

    [Test]
    public void InvalidJsonKeepsWhatWasRedactedBeforeTheError()
    {
        Assert.That(TrafficRedaction.Json("""{"token":"abc","x":"""), Is.EqualTo("""{"token":"(secret)","x":"""));
    }

    [Test]
    public void BytesThatAreNotUtf8AreBinary()
    {
        Assert.That(TrafficRedaction.Bytes([0xff, 0xfe, 0x00], new SecretRedactor()), Is.EqualTo("(binary, 3 bytes)"));
    }

    [Test]
    public async Task ProcessesAreLoggedWithArgumentsExitCodeAndOutput()
    {
        var events = new StringWriter();
        using var log = TestLogs.Writing(events);
        using var scope = log.UseAsCurrent();

        var result = await ProcessRunner.Instance.RunAsync("git", ["--version"]);

        var lines = TestLogs.Events(events.ToString());
        var sent = lines.Single(line => line.EventName() == "provider.request");
        var exited = lines.Single(line => line.EventName() == "provider.response");
        Assert.That(sent.GetProperty("Provider").GetString(), Is.EqualTo("process"));
        Assert.That(sent.GetProperty("@m").GetString(), Is.EqualTo("process git --version"));
        Assert.That(exited.GetProperty("ExitCode").GetInt32(), Is.EqualTo(result.ExitCode));
        Assert.That(exited.GetProperty("Stdout").GetString(), Is.EqualTo(result.Stdout));
    }
}