using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using RepoCli.Core.Features.Repo;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoCli.Core.Cli.Plugin;

public sealed record GeneratedFile(string Path, string Content);

public sealed record PluginDefinition(string Name, string Summary, string Version, string Skill);

public sealed record CommandUsage(string Usage, string Description);

public interface IPluginSource
{
    static abstract IReadOnlyList<GeneratedFile> Generate(string repoRoot);
}

public static class PluginGenerator
{
    public const string MarketplaceName = "repo-issues";
    public const string CommandName = "generate-plugin";
    const string MarketplaceDescription = "Claude Code plugins for the issues and docs CLIs (RepoIssuesCli, RepoDocsCli).";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<GeneratedFile> Generate(string repoRoot, PluginDefinition plugin)
    {
        var existing = ExistingMarketplace(repoRoot);
        var owner = existing?["owner"]?["name"]?.GetValue<string>() ?? GitOwner(repoRoot) ?? "RepoIssuesCli";

        var marketplace = new JsonObject
        {
            ["name"] = MarketplaceName,
            ["description"] = MarketplaceDescription,
            ["owner"] = new JsonObject { ["name"] = owner },
            ["plugins"] = MergePlugins(existing?["plugins"] as JsonArray, new JsonObject
            {
                ["name"] = plugin.Name,
                ["source"] = $"./plugins/{plugin.Name}",
                ["description"] = plugin.Summary,
                ["version"] = plugin.Version,
            }),
        };

        var manifest = new JsonObject
        {
            ["name"] = plugin.Name,
            ["version"] = plugin.Version,
            ["description"] = plugin.Summary,
            ["author"] = new JsonObject { ["name"] = owner },
        };

        return
        [
            new(".claude-plugin/marketplace.json", marketplace.ToJsonString(JsonOptions) + "\n"),
            new($"plugins/{plugin.Name}/.claude-plugin/plugin.json", manifest.ToJsonString(JsonOptions) + "\n"),
            new($"plugins/{plugin.Name}/skills/{plugin.Name}/SKILL.md", plugin.Skill.ReplaceLineEndings("\n").TrimEnd('\n') + "\n"),
        ];
    }

    public static void Write(string repoRoot, IEnumerable<GeneratedFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(repoRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
        }
    }

    public static string ExitCodeRows() => string.Join('\n', typeof(ExitCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => $"| {field.GetValue(null)} | {field.GetCustomAttribute<DescriptionAttribute>()?.Description} |"));

    public static string CommandRows(IEnumerable<CommandUsage> commands) =>
        string.Join('\n', commands.Select(command => $"| `{command.Usage}` | {command.Description} |"));

    public static string Values<T>() where T : struct, Enum => string.Join(", ", Enum.GetNames<T>().Select(name => $"`{name}`"));

    public static IReadOnlyList<CommandUsage> Commands(string toolName, Func<IAnsiConsole, CommandApp> createApp)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 100_000;
        createApp(console).Run(["cli", "xmldoc"]);

        return XDocument.Parse(writer.ToString()).Root!
            .Elements("Command")
            .Where(command => command.Attribute("Name")!.Value != CommandName)
            .Select(command => new CommandUsage(Usage(toolName, command), command.Element("Description")?.Value.Trim() ?? ""))
            .ToList();
    }

    static string Usage(string toolName, XElement command)
    {
        var arguments = command.Element("Parameters")?.Elements("Argument")
            .OrderBy(argument => (int)argument.Attribute("Position")!)
            .Select(argument => argument.Attribute("Required")!.Value == "true"
                ? $"<{argument.Attribute("Name")!.Value}>"
                : $"[{argument.Attribute("Name")!.Value}]") ?? [];
        return string.Join(' ', [toolName, command.Attribute("Name")!.Value, .. arguments]);
    }

    static JsonArray MergePlugins(JsonArray? existing, JsonObject plugin)
    {
        var name = plugin["name"]!.GetValue<string>();
        var merged = new JsonArray();
        var replaced = false;
        foreach (var entry in existing ?? [])
        {
            if (entry?["name"]?.GetValue<string>() == name)
            {
                merged.Add(plugin);
                replaced = true;
            }
            else if (entry is not null)
            {
                merged.Add(entry.DeepClone());
            }
        }

        if (!replaced)
            merged.Add(plugin);
        return merged;
    }

    static JsonNode? ExistingMarketplace(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".claude-plugin", "marketplace.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    static string? GitOwner(string repoRoot) => GitIdentity.Author(repoRoot)?.Split(" <")[0];
}
