using RepoInfraCli.Features.Infra.Hooks;

namespace RepoInfraCli.Tests.Hooks;

public class ShellGuardTests
{
    [TestCase("infra validate --json")]
    [TestCase("infra plan --env prod --json")]
    [TestCase("infra plan --exit-code --json")]
    [TestCase("infra show web-1 --field ipv4_address")]
    [TestCase("infra init")]
    [TestCase("infra restore api --list --json")]
    [TestCase("infra secrets list --json")]
    [TestCase("infra secrets key")]
    [TestCase("infra secrets generate postgres/api --length 40")]
    [TestCase("infra apply --help")]
    [TestCase("infra")]
    [TestCase("infra db")]
    [TestCase("cd .repo-infra && Get-Content infra.luau")]
    [TestCase("git commit -m \"add infra apply notes\"")]
    [TestCase("ls infra/")]
    [TestCase("dotnet build RepoIssuesCli.slnx -bl:msbuild.binlog")]
    [TestCase("dotnet test RepoInfraCli.Tests --filter ShellGuard")]
    [TestCase("dotnet run --project RepoInfraCli -- plan --json")]
    public void Allows(string command) =>
        Assert.That(ShellGuard.Classify(command).Decision, Is.EqualTo(GuardDecision.Allow), command);

    [TestCase("infra apply --yes --json")]
    [TestCase("infra apply --resources-only --allow-destroy --yes")]
    [TestCase("infra adopt web-1 --yes")]
    [TestCase("infra destroy --yes --confirm snappy-prod")]
    [TestCase("infra restore api --dump postgres/api/x.dump --yes")]
    [TestCase("infra db copy api --to local:api_dev --scrub scrub.sql")]
    [TestCase("infra secrets set prod/db/password --stdin")]
    [TestCase("infra secrets init")]
    [TestCase("infra secrets recipients remove ci-github")]
    [TestCase("infra plan --accept-new-hosts")]
    [TestCase("infra frobnicate")]
    [TestCase("infra.exe apply --yes")]
    [TestCase("& \"C:\\tools\\infra.exe\" apply --yes")]
    [TestCase("./bin/infra apply")]
    [TestCase("cd repo; infra validate; infra apply --yes")]
    [TestCase("infra validate && infra apply --yes")]
    [TestCase("infra plan --json | Out-File plan.json; infra apply --yes")]
    [TestCase("echo $(infra apply --yes)")]
    [TestCase("FOO=1 sudo infra apply --yes")]
    [TestCase("infra apply `\n  --yes")]
    [TestCase("bash -c \"infra plan\"")]
    [TestCase("Invoke-Expression 'infra plan'")]
    [TestCase("$cmd = 'infra'; & $cmd apply --yes")]
    [TestCase("dotnet RepoInfraCli/bin/Debug/net10.0/RepoInfraCli.dll apply --yes")]
    [TestCase("dotnet run --project RepoInfraCli apply")]
    [TestCase("dotnet tool run infra destroy")]
    public void Asks(string command) =>
        Assert.That(ShellGuard.Classify(command).Decision, Is.EqualTo(GuardDecision.Ask), command);

    [TestCase("infra secrets show postgres/api")]
    [TestCase("infra secrets show postgres/api --json")]
    [TestCase("infra apply --yes; infra secrets show postgres/api")]
    public void Denies(string command) =>
        Assert.That(ShellGuard.Classify(command).Decision, Is.EqualTo(GuardDecision.Deny), command);

    [TestCase("default", GuardDecision.Ask)]
    [TestCase("auto", GuardDecision.Ask)]
    [TestCase("acceptEdits", GuardDecision.Deny)]
    [TestCase("bypassPermissions", GuardDecision.Deny)]
    [TestCase("dontAsk", GuardDecision.Deny)]
    [TestCase("plan", GuardDecision.Deny)]
    [TestCase(null, GuardDecision.Deny)]
    public void AskTurnsIntoDenyWhereAPromptIsNotDocumented(string? mode, GuardDecision expected)
    {
        var verdict = ShellGuard.Decide("infra apply --yes", mode);

        Assert.That(verdict.Decision, Is.EqualTo(expected));
        if (expected == GuardDecision.Deny)
            Assert.That(verdict.Reason, Does.Contain("! infra apply --yes").And.Contain(mode ?? "unknown"));
    }

    [Test]
    public void ModeNeverLoosensADenyOrTightensAnAllow()
    {
        Assert.That(ShellGuard.Decide("infra secrets show x", "default").Decision, Is.EqualTo(GuardDecision.Deny));
        Assert.That(ShellGuard.Decide("infra plan", "bypassPermissions").Decision, Is.EqualTo(GuardDecision.Allow));
    }
}
