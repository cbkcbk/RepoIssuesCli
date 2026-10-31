using System.Net;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Tests.Cloud;

public class MarkerAndSigningTests
{
    [Test]
    public void TextMarkersRoundTripWithFlags()
    {
        var mark = new OwnerMark("snappy-prod", "api", Protect: true, Exported: true);

        Assert.That(mark.Text, Is.EqualTo("infra:snappy-prod name=api protect exported"));
        Assert.That(OwnerMark.Parse(mark.Text), Is.EqualTo(mark));
        Assert.That(OwnerMark.Parse("infra:snappy name=web-1"), Is.EqualTo(new OwnerMark("snappy", "web-1")));
        Assert.That(OwnerMark.Parse("managed by hand"), Is.Null);
        Assert.That(OwnerMark.Parse(null), Is.Null);
    }

    [Test]
    public void TagMarkersUseAnOwnerTagAndFlagTags()
    {
        var mark = new OwnerMark("snappy", "web-1", Protect: true);

        Assert.That(mark.Tags, Is.EqualTo(new[] { "infra:snappy", "infra-protect" }));
        Assert.That(OwnerMark.FromTags(["web", "infra:snappy", "infra-protect"], "web-1"), Is.EqualTo(mark));
        Assert.That(OwnerMark.FromTags(["web"], "web-1"), Is.Null);
        Assert.That(OwnerMark.IsMarkerTag("infra-exported") && !OwnerMark.IsMarkerTag("web"), Is.True);
    }

    [Test]
    public void NamePrefixMarkersBelongToTheirOwnerOnly()
    {
        var withEnv = new Marker("snappy", "prod");
        var plain = new Marker("snappy", null);

        Assert.That(OwnerMark.FromPrefixedName("snappy-prod-deploy", withEnv), Is.EqualTo(new OwnerMark("snappy-prod", "deploy")));
        Assert.That(OwnerMark.FromPrefixedName("snappy-staging-deploy", withEnv), Is.Null);
        Assert.That(OwnerMark.FromPrefixedName("deploy", plain), Is.Null);
        Assert.That(plain.PrefixedName("deploy"), Is.EqualTo("snappy-deploy"));
    }

    [Test]
    public void SigV4MatchesTheAwsGetBucketLifecycleExample()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?lifecycle");

        SigV4.Sign(request, [], "us-east-1", "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));

        Assert.That(string.Join(",", request.Headers.GetValues("Authorization")), Is.EqualTo(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543"));
    }

    [Test]
    public void SpacesBucketIsOwnedThroughItsMarkerObjectAndRequestsAreSigned()
    {
        using var harness = new CloudHarness();
        harness.Script("""
            cloud.spaces_bucket "test-backups" { region = "fra1", versioning = true, lifecycle = { { prefix = "postgres/", expire_days = 14 } } }
            """);
        const string bucket = "https://fra1.digitaloceanspaces.com/test-backups";
        harness.Http
            .On("GET", "https://fra1.digitaloceanspaces.com/", FakeCloud.Fixture("spaces-buckets.xml"))
            .On("GET", $"{bucket}/.infra-owner", "infra:test name=test-backups")
            .On("GET", $"{bucket}?versioning=", """<VersioningConfiguration xmlns="http://s3.amazonaws.com/doc/2006-03-01/"><Status>Enabled</Status></VersioningConfiguration>""")
            .On("GET", $"{bucket}?lifecycle=", "<Error><Code>NoSuchLifecycleConfiguration</Code></Error>", HttpStatusCode.NotFound)
            .On("GET", $"{bucket}?acl=", """<AccessControlPolicy><AccessControlList><Grant><Grantee><ID>6174283</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>""")
            .On("PUT", $"{bucket}?lifecycle=", "");

        var plan = harness.Run("plan", "--resources-only");
        var apply = harness.Run("apply", "--resources-only", "--yes");

        Assert.That(CloudHarness.Lines(plan.Output), Does.Contain("  ~ digitalocean spaces_bucket test-backups: lifecycle [] -> [postgres/ 14d]"), plan.Output + plan.Errors);
        Assert.That(apply.ExitCode, Is.EqualTo(InfraExitCodes.Success), apply.Output + apply.Errors);
        var put = harness.Http.Sent("PUT", "lifecycle").Single();
        Assert.That(put.Body, Does.Contain("<Prefix>postgres/</Prefix>").And.Contain("<Days>14</Days>"));
        Assert.That(put.Headers["Authorization"], Does.StartWith("AWS4-HMAC-SHA256 Credential=DO00EXAMPLEACCESS/").And.Contain("/fra1/s3/aws4_request"));
        Assert.That(put.Headers["Authorization"], Does.Contain("SignedHeaders=content-md5;host;x-amz-content-sha256;x-amz-date"));
    }
}
