using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Plugin;

[Description("Debug builds only: regenerate the Claude Code plugin marketplace and skill in the repository.")]
public sealed class GeneratePluginCommand(IAnsiConsole console) : Command<GlobalSettings>
{
    public const string Name = "generate-plugin";

    public override int Execute(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        var root = IssueStore.Open(settings.Root).Root;
        var files = PluginGenerator.Generate(root);
        PluginGenerator.Write(root, files);

        foreach (var file in files)
            console.MarkupLine($"Wrote [bold]{Markup.Escape(file.Path)}[/]");

        return ExitCodes.Success;
    }
}
