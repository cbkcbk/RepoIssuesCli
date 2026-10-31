using System.Text.Json;
using RepoInfraCli.Cli;

namespace RepoInfraCli.Tests.Cli;

public class ValidateCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    [Test]
    public void DemoValidates()
    {
        repo.CopyDemo();

        var result = repo.Run("validate");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
        Assert.That(result.Output, Does.Contain("repo-unavailable").And.Contain("platform-infra").And.EndWith("1 warning(s) found.\n"));
    }

    [Test]
    public void DemoFolderValidatesInPlace()
    {
        var demoRoot = Path.GetDirectoryName(Path.GetDirectoryName(TempRepo.DemoEntry))!;

        var result = TempRepo.RunWithoutRoot("validate", "--root", demoRoot);

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Success), result.Output + result.Errors);
    }

    [Test]
    public void DemoJsonReportsValidWithSummary()
    {
        repo.CopyDemo();

        var result = repo.Run("validate", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;
        var summary = json.GetProperty("summary");

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(json.EnumerateObject().Select(property => property.Name), Is.EqualTo(new[] { "valid", "errors", "warnings", "summary" }));
        Assert.That(json.GetProperty("valid").GetBoolean(), Is.True);
        Assert.That(json.GetProperty("errors").GetArrayLength(), Is.Zero);
        Assert.That(json.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetProperty("code").GetString()), Is.EqualTo(new[] { "repo-unavailable" }));
        Assert.That(summary.GetProperty("resources").GetInt32(), Is.EqualTo(11));
        Assert.That(summary.GetProperty("hosts").GetInt32(), Is.EqualTo(3));
        Assert.That(summary.GetProperty("plays").GetInt32(), Is.EqualTo(5));
        Assert.That(summary.GetProperty("apps").GetInt32(), Is.EqualTo(1));
        Assert.That(summary.GetProperty("tasks").GetInt32(), Is.GreaterThan(20));
        Assert.That(summary.GetProperty("secrets").GetInt32(), Is.EqualTo(7));
    }

    [Test]
    public void VerbosePrintsTheGraphOutline()
    {
        repo.CopyDemo();

        var result = repo.Run("validate", "-v");

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output, Does.Contain("Project snappy")
            .And.Contain("digitalocean droplet web-1 exported")
            .And.Contain("baseline: 5 task(s)")
            .And.Contain("platform-infra/node_exporter: (external)")
            .And.Contain("1 warning(s) found."));
    }

    [Test]
    public void DoubleVerboseAlsoPrintsDebugEventsToStderr()
    {
        repo.CopyDemo();

        var quiet = repo.Run("validate", "-v");
        var loud = repo.Run("validate", "-vv");

        Assert.That(quiet.Errors, Does.Not.Contain("Evaluated"));
        Assert.That(loud.Errors, Does.Contain("Evaluated .repo-infra/infra.luau"));
    }

    [Test]
    public void ErrorsExitOneAndShowFileAndLine()
    {
        repo.WriteScript("""
                         local h = infra.host "a" { address = "10.0.0.1", user = "root", colour = "blue" }
                         """);

        var result = repo.Run("validate");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("unknown-option").And.Contain("'colour'").And.Contain(".repo-infra/infra.luau:3").And.Contain("1 error(s) found."));
    }

    [Test]
    public void JsonErrorsCarryCodeFileAndLine()
    {
        repo.WriteScript("""
                         infra.host "a" { user = "root" }
                         """);

        var result = repo.Run("validate", "--json");
        var json = JsonDocument.Parse(result.Output).RootElement;
        var error = json.GetProperty("errors")[0];

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(json.GetProperty("valid").GetBoolean(), Is.False);
        Assert.That(error.GetProperty("code").GetString(), Is.EqualTo("missing-option"));
        Assert.That(error.GetProperty("message").GetString(), Does.Contain("'address'"));
        Assert.That(error.GetProperty("file").GetString(), Is.EqualTo(".repo-infra/infra.luau"));
        Assert.That(error.GetProperty("line").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public void ScriptErrorsAreReportedWithTheirLine()
    {
        repo.WriteScript("""
                         local x = nil
                         x.y = 1
                         """);

        var json = JsonDocument.Parse(repo.Run("validate", "--json").Output).RootElement;
        var error = json.GetProperty("errors")[0];

        Assert.That(error.GetProperty("code").GetString(), Is.EqualTo("script-error"));
        Assert.That(error.GetProperty("line").GetInt32(), Is.EqualTo(4));
        Assert.That(json.TryGetProperty("summary", out _), Is.False);
    }

    [Test]
    public void SyntaxErrorsAreScriptErrors()
    {
        repo.WriteScript("local = 1\n");

        var result = repo.Run("validate", "--json");
        var error = JsonDocument.Parse(result.Output).RootElement.GetProperty("errors")[0];

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(error.GetProperty("code").GetString(), Is.EqualTo("script-error"));
        Assert.That(error.GetProperty("line").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public void NotInitializedIsAUsageError()
    {
        var result = repo.Run("validate");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("infra init"));
    }

    [Test]
    public void EnvIsRequiredWhenEnvsFolderExists()
    {
        repo.WriteScript("");
        repo.WriteFile(".repo-infra/envs/prod/.keep", "");
        repo.WriteFile(".repo-infra/envs/staging/.keep", "");

        var result = repo.Run("validate");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Errors, Does.Contain("--env is required").And.Contain("prod, staging"));
    }

    [Test]
    public void KnownEnvValidatesAndUnknownEnvIsAnError()
    {
        repo.WriteScript("""
                         assert(infra.env == "prod", "env is " .. tostring(infra.env))
                         """);
        repo.WriteFile(".repo-infra/envs/prod/.keep", "");

        var known = repo.Run("validate", "--env", "prod");
        var unknown = repo.Run("validate", "--env", "qa", "--json");

        Assert.That(known.ExitCode, Is.Zero, known.Output);
        Assert.That(unknown.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(JsonDocument.Parse(unknown.Output).RootElement.GetProperty("errors")[0].GetProperty("code").GetString(), Is.EqualTo("unknown-env"));
    }

    [Test]
    public void EnvWithoutEnvsFolderIsAnError()
    {
        repo.WriteScript("");

        var result = repo.Run("validate", "--env", "prod");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Invalid));
        Assert.That(result.Output, Does.Contain("unknown-env"));
    }

    [Test]
    public void LogAppendsEventsAsJsonLines()
    {
        repo.CopyDemo();
        var log = Path.Combine(repo.Root, "logs", "infra.jsonl");

        var result = repo.Run("validate", "--log", log);

        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var events = File.ReadAllLines(log).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.That(events.Select(entry => entry.GetProperty("EventName").GetString()), Does.Contain("command.start").And.Contain("evaluate.done").And.Contain("command.end"));
        Assert.That(events.All(entry => entry.TryGetProperty("@t", out _) && entry.TryGetProperty("@m", out _)), Is.True);
        Assert.That(events.Select(entry => entry.GetProperty("RunId").GetString()).Distinct().Count(), Is.EqualTo(1));
        Assert.That(events.All(entry => entry.GetProperty("Command").GetString() == "validate"), Is.True);
    }

    [Test]
    public void UnknownCommandIsAUsageError()
    {
        var result = repo.Run("nope");

        Assert.That(result.ExitCode, Is.EqualTo(InfraExitCodes.Usage));
        Assert.That(result.Output, Is.Empty);
    }

    [Test]
    public void ExitCodesKeepTheirDocumentedValues()
    {
        Assert.That(new[] { InfraExitCodes.Success, InfraExitCodes.Invalid, InfraExitCodes.Usage, InfraExitCodes.NotFound, InfraExitCodes.Unexpected, InfraExitCodes.PartiallyApplied, InfraExitCodes.Drift },
            Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5, 6 }));
    }

    [TestCase(new[] { "validate", "-v" }, 1)]
    [TestCase(new[] { "validate", "-vv" }, 2)]
    [TestCase(new[] { "validate", "-v", "--verbose" }, 2)]
    public void VerbosityCountsRepeatedFlags(string[] arguments, int expected) =>
        Assert.That(InfraCommand<InfraSettings>.Verbosity(arguments), Is.EqualTo(expected));
}