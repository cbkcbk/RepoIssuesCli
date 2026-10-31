using System.Net;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Cloud.DigitalOcean;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Tests.Cloud;

public class MarkerAndSigningTests
{
    [Test]
    public void TextMarkersRoundTripWithEnvAndFlags()
    {
        var mark = new OwnerMark("snappy", "prod", "api", Protect: true, Exported: true);

        Assert.That(mark.Text, Is.EqualTo("infra:snappy env=prod name=api protect exported"));
        Assert.That(OwnerMark.Parse(mark.Text), Is.EqualTo(mark));
        Assert.That(OwnerMark.Parse("infra:snappy name=web-1"), Is.EqualTo(new OwnerMark("snappy", null, "web-1")));
        Assert.That(OwnerMark.Parse("infra:snappy env=prod"), Is.Null);
        Assert.That(OwnerMark.Parse("managed by hand"), Is.Null);
        Assert.That(OwnerMark.Parse(null), Is.Null);
    }

    [Test]
    public void TextMarkersMatchTheExactProjectAndEnv()
    {
        var plain = OwnerMark.Parse("infra:snappy name=api")!;
        var prod = OwnerMark.Parse("infra:snappy env=prod name=api")!;
        var hyphenated = OwnerMark.Parse("infra:snappy-prod name=api")!;

        Assert.That(plain.BelongsTo(new Marker("snappy", null)), Is.True);
        Assert.That(plain.BelongsTo(new Marker("snappy", "prod")), Is.False);
        Assert.That(prod.BelongsTo(new Marker("snappy", "prod")), Is.True);
        Assert.That(prod.BelongsTo(new Marker("snappy", null)), Is.False);
        Assert.That(prod.BelongsTo(new Marker("snappy", "staging")), Is.False);
        Assert.That(hyphenated.BelongsTo(new Marker("snappy", "prod")), Is.False);
    }

    [Test]
    public void TagMarkersSplitProjectEnvAndFlags()
    {
        var plain = new OwnerMark("snappy", null, "web-1", Protect: true);
        var prod = new OwnerMark("snappy", "prod", "web-1", Exported: true);

        Assert.That(plain.Tags, Is.EqualTo(new[] { "infra:snappy", "infra-protect" }));
        Assert.That(prod.Tags, Is.EqualTo(new[] { "infra:snappy", "infra-env:prod", "infra-exported" }));
        Assert.That(OwnerMark.FromTags(["web", "infra:snappy", "infra-protect"], "web-1"), Is.EqualTo(plain));
        Assert.That(OwnerMark.FromTags(["infra-env:prod", "web", "infra:snappy", "infra-exported"], "web-1"), Is.EqualTo(prod));
        Assert.That(OwnerMark.FromTags(["infra:snappy", "infra-env:prod"], "web-1")!.BelongsTo(new Marker("snappy", null)), Is.False);
        Assert.That(OwnerMark.FromTags(["web"], "web-1"), Is.Null);
        Assert.That(new[] { "infra-exported", "infra-env:prod", "infra:snappy" }.All(OwnerMark.IsMarkerTag) && !OwnerMark.IsMarkerTag("web"), Is.True);
    }

    [Test]
    public void NamePrefixMarkersBelongToTheirProjectAndEnvOnly()
    {
        var withEnv = new Marker("snappy", "prod");
        var plain = new Marker("snappy", null);

        Assert.That(withEnv.PrefixedName("deploy"), Is.EqualTo("snappy--prod--deploy"));
        Assert.That(plain.PrefixedName("deploy"), Is.EqualTo("snappy--deploy"));
        Assert.That(OwnerMark.FromPrefixedName("snappy--prod--deploy", withEnv), Is.EqualTo(new OwnerMark("snappy", "prod", "deploy")));
        Assert.That(OwnerMark.FromPrefixedName("snappy--deploy", plain), Is.EqualTo(new OwnerMark("snappy", null, "deploy")));
        Assert.That(OwnerMark.FromPrefixedName("snappy--prod--deploy", plain), Is.Null);
        Assert.That(OwnerMark.FromPrefixedName("snappy--deploy", withEnv), Is.Null);
        Assert.That(OwnerMark.FromPrefixedName("snappy--staging--deploy", withEnv), Is.Null);
        Assert.That(OwnerMark.FromPrefixedName("snappy-prod-deploy", withEnv), Is.Null);
        Assert.That(OwnerMark.FromPrefixedName("deploy", plain), Is.Null);
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

    [Test]
    public void SpacesMarkerObjectCarriesProjectEnvAndNameTags()
    {
        using var harness = new CloudHarness();
        harness.Repo.WriteInfra($$"""
                                  local infra = require("@infra")
                                  infra.project "test" {}
                                  {{CloudHarness.Providers}}
                                  cloud.spaces_bucket "test-media" { region = "fra1" }
                                  """);
        harness.Repo.WriteFile(".repo-infra/envs/prod/.keep", "");
        const string bucket = "https://fra1.digitaloceanspaces.com/test-media";
        harness.EmptyAccount().Http
            .On("PUT", bucket, "")
            .On("PUT", $"{bucket}/.infra-owner", "")
            .On("GET", $"{bucket}?versioning=", "<VersioningConfiguration/>")
            .On("GET", $"{bucket}?lifecycle=", "<Error><Code>NoSuchLifecycleConfiguration</Code></Error>", HttpStatusCode.NotFound)
            .On("GET", $"{bucket}?acl=", "<AccessControlPolicy/>");

        var apply = harness.Run("apply", "--resources-only", "--yes", "--env", "prod");

        Assert.That(apply.ExitCode, Is.EqualTo(InfraExitCodes.Success), apply.Output + apply.Errors);
        var marker = harness.Http.Sent("PUT", ".infra-owner").Single();
        Assert.That(marker.Body, Is.EqualTo("infra:test env=prod name=test-media"));
        Assert.That(marker.Headers["x-amz-tagging"], Is.EqualTo("infra-project=test&infra-env=prod&infra-name=test-media"));
    }

    [Test]
    public void BucketsNeedSpacesKeysOnTheProvider()
    {
        using var harness = new CloudHarness();
        harness.Repo.WriteInfra("""
                                local infra = require("@infra")
                                infra.project "test" {}
                                local digitalocean = require("@infra/digitalocean")
                                local cloud = digitalocean.provider { token = infra.secret "digitalocean/token" }
                                cloud.spaces_bucket "test-media" { region = "fra1" }
                                """);
        harness.EmptyAccount();

        var plan = harness.Run("plan", "--resources-only");

        Assert.That(plan.ExitCode, Is.EqualTo(InfraExitCodes.Invalid), plan.Output + plan.Errors);
        Assert.That(plan.Output + plan.Errors, Does.Contain("digitalocean.provider has no spaces_access_key"));
        Assert.That(harness.Http.Requests.Where(request => request.Url.Contains("digitaloceanspaces", StringComparison.Ordinal)), Is.Empty);
    }
}