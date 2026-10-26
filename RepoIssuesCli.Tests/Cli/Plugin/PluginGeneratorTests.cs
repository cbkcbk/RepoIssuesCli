using System.Text.Json.Nodes;
using RepoCli.Core.Cli.Plugin;
using RepoIssuesCli.Cli.Plugin;
using Spectre.Console.Testing;

namespace RepoIssuesCli.Tests.Cli.Plugin;

public class PluginGeneratorTests
{
    [Test]
    public void WritesMarketplacePluginAndSkill()
    {
        using var repo = new TempRepo();

        var result = repo.Run(PluginGenerator.CommandName);

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
    public void KeepsWrittenPathsOnOneLine()
    {
        using var repo = new TempRepo();

        var result = repo.Run(new TestConsole().Width(20), PluginGenerator.CommandName);

        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        var expected = IssuesPlugin.Generate(repo.Root).Select(file => $"Wrote {file.Path}");
        Assert.That(result.Output.TrimEnd('\n').Split('\n'), Is.EqualTo(expected));
    }

    [Test]
    public void SkillListsEveryCommandExceptTheGenerator()
    {
        using var repo = new TempRepo();

        var skill = IssuesPlugin.Generate(repo.Root).Single(file => file.Path.EndsWith("SKILL.md")).Content;

        Assert.That(skill, Does.StartWith("---\nname: issues\ndescription: "));
        foreach (var command in new[] { "init", "new [name]", "list", "show [ref]", "edit [ref]", "status [ref] [status]", "delete [ref]", "validate" })
            Assert.That(skill, Does.Contain($"`issues {command}`"));
        Assert.That(skill, Does.Contain("| 3 | referenced item not found |"));
        Assert.That(skill, Does.Contain("`InProgress`"));
        Assert.That(skill, Does.Contain("--author \"Claude\""));
        Assert.That(skill, Does.Not.Contain($"| `issues {PluginGenerator.CommandName}"));
    }

    [Test]
    public void KeepsExistingOwner()
    {
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(repo.Root, ".claude-plugin", "marketplace.json"), """{ "owner": { "name": "Kept Owner" } }""");

        var marketplace = IssuesPlugin.Generate(repo.Root)[0].Content;

        Assert.That(marketplace, Does.Contain("Kept Owner"));
    }
}