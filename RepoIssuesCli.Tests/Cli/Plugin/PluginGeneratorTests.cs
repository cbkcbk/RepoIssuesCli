using System.Text.Json.Nodes;
using RepoIssuesCli.Cli.Plugin;

namespace RepoIssuesCli.Tests.Cli.Plugin;

public class PluginGeneratorTests
{
    [Test]
    public void WritesMarketplacePluginAndSkill()
    {
        using var repo = new TempRepo();

        var result = repo.Run(GeneratePluginCommand.Name);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(repo.Root, ".claude-plugin", "marketplace.json")))!;
        var entry = marketplace["plugins"]![0]!;
        Assert.That(marketplace["name"]!.GetValue<string>(), Is.EqualTo(PluginGenerator.MarketplaceName));
        Assert.That(entry["source"]!.GetValue<string>(), Is.EqualTo("./plugins/issues"));

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(repo.Root, "plugins", "issues", ".claude-plugin", "plugin.json")))!;
        Assert.That(manifest["name"]!.GetValue<string>(), Is.EqualTo(entry["name"]!.GetValue<string>()));
        Assert.That(manifest["version"]!.GetValue<string>(), Is.EqualTo(entry["version"]!.GetValue<string>()));
    }

    [Test]
    public void SkillListsEveryCommandExceptTheGenerator()
    {
        using var repo = new TempRepo();

        var skill = PluginGenerator.Generate(repo.Root).Single(file => file.Path.EndsWith("SKILL.md")).Content;

        Assert.That(skill, Does.StartWith("---\nname: issues\ndescription: "));
        foreach (var command in new[] { "init", "new [name]", "list", "show [ref]", "edit [ref]", "status [ref] [status]", "delete [ref]", "validate" })
            Assert.That(skill, Does.Contain($"`issues {command}`"));
        Assert.That(skill, Does.Contain("| 3 | issue not found |"));
        Assert.That(skill, Does.Contain("`InProgress`"));
        Assert.That(skill, Does.Not.Contain($"| `issues {GeneratePluginCommand.Name}"));
    }

    [Test]
    public void KeepsExistingOwner()
    {
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(repo.Root, ".claude-plugin", "marketplace.json"), """{ "owner": { "name": "Kept Owner" } }""");

        var marketplace = PluginGenerator.Generate(repo.Root)[0].Content;

        Assert.That(marketplace, Does.Contain("Kept Owner"));
    }
}
