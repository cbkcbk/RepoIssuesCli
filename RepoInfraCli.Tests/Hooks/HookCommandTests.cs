using System.Text.Json.Nodes;
using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Hooks;
using RepoInfraCli.Features.Infra.Repos;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Hooks;

public class HookCommandTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    RunResult Hook(string hookEvent, string stdin)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors, new StringReader(stdin)).Run(["hook", hookEvent]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    static string ShellInput(string command, string mode = "default") =>
        new JsonObject { ["tool_name"] = "PowerShell", ["permission_mode"] = mode, ["tool_input"] = new JsonObject { ["command"] = command } }.ToJsonString();

    string EditInput(string relativePath) =>
        new JsonObject { ["cwd"] = repo.Root, ["tool_input"] = new JsonObject { ["file_path"] = Path.Combine(repo.Root, relativePath) } }.ToJsonString();

    static JsonNode Decision(RunResult result) => JsonNode.Parse(result.Output)!["hookSpecificOutput"]!;

    [Test]
    public void ShellAsksBeforeApply()
    {
        var result = Hook("shell", ShellInput("infra apply --yes"));

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(Decision(result)["hookEventName"]!.GetValue<string>(), Is.EqualTo("PreToolUse"));
        Assert.That(Decision(result)["permissionDecision"]!.GetValue<string>(), Is.EqualTo("ask"));
        Assert.That(Decision(result)["permissionDecisionReason"]!.GetValue<string>(), Does.Contain("infra apply"));
    }

    [Test]
    public void ShellDeniesApplyInBypassMode()
    {
        var result = Hook("shell", ShellInput("infra apply --yes", "bypassPermissions"));

        Assert.That(Decision(result)["permissionDecision"]!.GetValue<string>(), Is.EqualTo("deny"));
        Assert.That(Decision(result)["permissionDecisionReason"]!.GetValue<string>(), Does.Contain("! infra apply --yes"));
    }

    [Test]
    public void ShellReadsInputWithAByteOrderMark() =>
        Assert.That(Decision(Hook("shell", "﻿" + ShellInput("infra apply --yes")))["permissionDecision"]!.GetValue<string>(), Is.EqualTo("ask"));

    [Test]
    public void ShellSaysNothingForReadOnlyCommands() =>
        Assert.That(Hook("shell", ShellInput("infra plan --json")).Output, Is.Empty);

    [Test]
    public void ShellDeniesWhenTheInputCannotBeRead()
    {
        var result = Hook("shell", "not json");

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(Decision(result)["permissionDecision"]!.GetValue<string>(), Is.EqualTo("deny"));
    }

    [Test]
    public void EditBlocksOnErrorsWithFileAndLine()
    {
        repo.WriteScript("""infra.host "a" { user = "root" }""");

        var result = Hook("edit", EditInput(".repo-infra/infra.luau"));
        var output = JsonNode.Parse(result.Output)!;

        Assert.That(result.ExitCode, Is.Zero);
        Assert.That(output["decision"]!.GetValue<string>(), Is.EqualTo("block"));
        Assert.That(output["reason"]!.GetValue<string>(), Does.Contain("- .repo-infra/infra.luau:3 missing-option"));
    }

    [Test]
    public void EditIsSilentWhenValidAndForFilesOutsideRepoInfra()
    {
        repo.WriteScript("""infra.host "a" { address = "10.0.0.1", user = "root" }""");
        repo.WriteFile("src/notes.luau", "");

        Assert.That(Hook("edit", EditInput(".repo-infra/infra.luau")).Output, Is.Empty);
        Assert.That(Hook("edit", EditInput("src/notes.luau")).Output, Is.Empty);
        Assert.That(Hook("edit", EditInput(".repo-infra/secrets.yaml")).Output, Is.Empty);
    }

    [Test]
    public void EditChecksEveryEnvAndNamesIt()
    {
        repo.WriteScript("""
                         if infra.env == "prod" then
                           infra.host "a" { user = "root" }
                         end
                         """);
        repo.WriteFile(".repo-infra/envs/prod/.keep", "");
        repo.WriteFile(".repo-infra/envs/staging/.keep", "");

        var reason = JsonNode.Parse(Hook("edit", EditInput(".repo-infra/infra.luau")).Output)!["reason"]!.GetValue<string>();

        Assert.That(reason, Does.Contain("[prod]").And.Not.Contain("[staging]"));
    }

    [Test]
    public void EditUsesOnlyCachedReposAndKeepsQuietAboutUncachedOnes()
    {
        using var remote = new GitRemote();
        remote.WriteInfra("local infra = require(\"@infra\")\ninfra.project \"platform\" {}\nreturn {}\n");
        remote.Commit();
        repo.WriteScript($"local repo = require(\"@infra/repo\")\nlocal platform = repo.provider {{ url = \"{remote.Url}\", ref = \"main\" }}\n");
        var cacheRoot = Path.Combine(repo.Root, "cache");

        var output = InfraHooks.Edit(HookInput.Parse(EditInput(".repo-infra/infra.luau"))!, null, new RepoCache(cacheRoot) { Offline = true });

        Assert.That(output, Is.Null);
        Assert.That(Directory.Exists(cacheRoot), Is.False, "nothing was cloned");
    }
}