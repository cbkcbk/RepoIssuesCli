using System.Text.Json.Nodes;
using RepoCli.Core.Cli.Plugin;

namespace RepoCli.Core.Tests.Cli.Plugin;

public class PluginGeneratorTests
{
    static PluginDefinition Plugin(string name, string version = "1.0.0") => new(name, $"{name} summary", version, $"---\nname: {name}\n---\n");

    static JsonArray Plugins(IReadOnlyList<GeneratedFile> files) =>
        JsonNode.Parse(files.Single(file => file.Path == ".claude-plugin/marketplace.json").Content)!["plugins"]!.AsArray();

    [Test]
    public void SecondPluginIsAddedBesideTheFirst()
    {
        using var folder = new TempFolder();
        PluginGenerator.Write(folder.Root, PluginGenerator.Generate(folder.Root, Plugin("issues")));

        var plugins = Plugins(PluginGenerator.Generate(folder.Root, Plugin("docs")));

        Assert.That(plugins.Select(plugin => plugin!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "issues", "docs" }));
    }

    [Test]
    public void RegeneratingAPluginReplacesItsEntryInPlace()
    {
        using var folder = new TempFolder();
        PluginGenerator.Write(folder.Root, PluginGenerator.Generate(folder.Root, Plugin("issues")));
        PluginGenerator.Write(folder.Root, PluginGenerator.Generate(folder.Root, Plugin("docs")));

        var plugins = Plugins(PluginGenerator.Generate(folder.Root, Plugin("issues", "2.0.0")));

        Assert.That(plugins, Has.Count.EqualTo(2));
        Assert.That(plugins[0]!["version"]!.GetValue<string>(), Is.EqualTo("2.0.0"));
    }

    [Test]
    public void SkillEndsWithASingleNewline()
    {
        using var folder = new TempFolder();

        var skill = PluginGenerator.Generate(folder.Root, Plugin("docs")).Single(file => file.Path.EndsWith("SKILL.md")).Content;

        Assert.That(skill, Is.EqualTo("---\nname: docs\n---\n"));
    }
}
