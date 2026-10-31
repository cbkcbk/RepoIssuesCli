using System.Text.Json.Nodes;
using RepoCli.Core.Cli.Plugin;
using RepoInfraCli.Cli.Plugin;

namespace RepoInfraCli.Tests.Cli.Plugin;

public class PluginGeneratorTests
{
    TempRepo repo = null!;

    [SetUp]
    public void SetUp() => repo = new TempRepo();

    [TearDown]
    public void TearDown() => repo.Dispose();

    string Generated(string suffix) => InfraPlugin.Generate(repo.Root).Single(file => file.Path.EndsWith(suffix)).Content;

    [Test]
    public void WritesPluginSkillAndHooks()
    {
        var result = repo.Run(PluginGenerator.CommandName);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var manifest = JsonNode.Parse(repo.ReadFile("plugins/infra/.claude-plugin/plugin.json"))!;
        Assert.That(manifest["name"]!.GetValue<string>(), Is.EqualTo(InfraPlugin.Name));
        Assert.That(File.Exists(Path.Combine(repo.Root, "plugins/infra/skills/infra/SKILL.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(repo.Root, "plugins/infra", InfraPlugin.HooksPath)), Is.True);
        var marketplace = JsonNode.Parse(repo.ReadFile(".claude-plugin/marketplace.json"))!;
        Assert.That(marketplace["description"]!.GetValue<string>(), Does.Contain("issues, docs and infra CLIs"));
    }

    [Test]
    public void SkillListsBranchCommandsAndTheInfraExitCodes()
    {
        var skill = Generated("SKILL.md");

        Assert.That(skill, Does.StartWith("---\nname: infra\ndescription: "));
        foreach (var command in new[] { "validate", "plan", "apply", "destroy [NAMES]", "restore <DATABASE>", "db copy <DATABASE>", "secrets set <NAME>", "secrets recipients add <NAME> <PUBKEY>", "hook <event>" })
            Assert.That(skill, Does.Contain($"| `infra {command}"), command);
        Assert.That(skill, Does.Not.Contain("| `infra db` |").And.Not.Contain($"| `infra {PluginGenerator.CommandName}"));
        Assert.That(skill, Does.Contain("| 5 | partially applied").And.Contain("| 6 | drift"));
        Assert.That(skill, Does.Contain(".repo-infra/.luau/@infra/").And.Contain("infra secrets show").And.Contain("infra plan --json"));
    }

    [Test]
    public void HooksGuardTheShellToolsAndValidateEdits()
    {
        var hooks = JsonNode.Parse(Generated(InfraPlugin.HooksPath))!["hooks"]!.AsObject();

        Assert.That(hooks.Select(hook => hook.Key), Is.EqualTo(new[] { "PreToolUse", "PostToolUse" }));
        Assert.That(hooks["PreToolUse"]![0]!["matcher"]!.GetValue<string>(), Is.EqualTo("Bash|PowerShell"));
        Assert.That(hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>(), Is.EqualTo("infra hook shell"));
        Assert.That(hooks["PostToolUse"]![0]!["matcher"]!.GetValue<string>(), Is.EqualTo("Edit|Write|MultiEdit"));
        Assert.That(hooks["PostToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>(), Is.EqualTo("infra hook edit"));
    }
}