using System.Text.Json.Nodes;
using RepoCli.Core.Cli.Plugin;
using RepoDocsCli.Cli.Plugin;

namespace RepoDocsCli.Tests.Cli.Plugin;

public class PluginGeneratorTests
{
    static string FileContent(TempRepo repo, string path) => File.ReadAllText(Path.Combine(repo.Root, path));

    [Test]
    public void WritesPluginSkillAndHooks()
    {
        using var repo = new TempRepo();

        var result = repo.Run(PluginGenerator.CommandName);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var manifest = JsonNode.Parse(FileContent(repo, "plugins/docs/.claude-plugin/plugin.json"))!;
        Assert.That(manifest["name"]!.GetValue<string>(), Is.EqualTo(DocsPlugin.Name));
        Assert.That(File.Exists(Path.Combine(repo.Root, "plugins/docs/skills/docs/SKILL.md")), Is.True);
        Assert.That(File.Exists(Path.Combine(repo.Root, "plugins/docs", DocsPlugin.HooksPath)), Is.True);
    }

    [Test]
    public void SkillHasTheNoDirectEditRuleAndEveryCommandExceptTheGenerator()
    {
        using var repo = new TempRepo();

        var skill = DocsPlugin.Generate(repo.Root).Single(file => file.Path.EndsWith("SKILL.md")).Content;

        Assert.That(skill, Does.StartWith("---\nname: docs\ndescription: "));
        Assert.That(skill, Does.Contain("**Never edit, create, rename or delete files in `.repo-docs/` directly**"));
        Assert.That(skill, Does.Contain("tell the user instead of editing the YAML"));
        Assert.That(skill, Does.Contain("resolving a git merge conflict"));
        foreach (var command in new[]
                 {
                     "init", "render", "check", "prune", "show <address>", "for <path>", "find <query>", "list", "touch [target]", "add [target]", "edit [target]", "rm [target]",
                     "affected", "import [files]", "budget", "hook <event>",
                 })
            Assert.That(skill, Does.Contain($"| `docs {command}`"));
        Assert.That(skill, Does.Contain("| 3 | referenced item not found |"));
        Assert.That(skill, Does.Contain("| `plan` |").And.Contain("when-feature-active"));
        Assert.That(skill, Does.Contain("--step-issues 1=23,24").And.Contain("--add-anchor path:line").And.Contain("docs prune --json"));
        Assert.That(skill, Does.Not.Contain($"| `docs {PluginGenerator.CommandName}"));
    }

    [Test]
    public void HooksJsonRunsTheDocsHookForEachEvent()
    {
        using var repo = new TempRepo();

        var hooks = JsonNode.Parse(DocsPlugin.Generate(repo.Root).Single(file => file.Path.EndsWith(DocsPlugin.HooksPath)).Content)!["hooks"]!.AsObject();

        Assert.That(hooks.Select(hook => hook.Key), Is.EqualTo(new[] { "SessionStart", "PostToolUse", "Stop" }));
        string Command(string hookEvent, int index = 0) => hooks[hookEvent]![index]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.That(Command("SessionStart"), Is.EqualTo("docs hook session-start"));
        Assert.That(hooks["PostToolUse"]![0]!["matcher"]!.GetValue<string>(), Is.EqualTo("Read"));
        Assert.That(Command("PostToolUse"), Is.EqualTo("docs hook read"));
        Assert.That(hooks["PostToolUse"]![1]!["matcher"]!.GetValue<string>(), Is.EqualTo("Edit|Write|MultiEdit"));
        Assert.That(Command("PostToolUse", 1), Is.EqualTo("docs hook edit"));
        Assert.That(Command("Stop"), Is.EqualTo("docs hook stop"));
    }

    [Test]
    public void MarketplaceKeepsTheIssuesPlugin()
    {
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(repo.Root, ".claude-plugin", "marketplace.json"),
            """{ "owner": { "name": "Kept Owner" }, "plugins": [{ "name": "issues", "source": "./plugins/issues" }] }""");

        Assert.That(repo.Run(PluginGenerator.CommandName).ExitCode, Is.Zero);

        var marketplace = JsonNode.Parse(FileContent(repo, ".claude-plugin/marketplace.json"))!;
        Assert.That(marketplace["plugins"]!.AsArray().Select(plugin => plugin!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "issues", "docs" }));
        Assert.That(marketplace["owner"]!["name"]!.GetValue<string>(), Is.EqualTo("Kept Owner"));
    }
}
