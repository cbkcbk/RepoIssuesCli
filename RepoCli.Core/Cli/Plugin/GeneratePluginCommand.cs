using System.ComponentModel;
using RepoCli.Core.Features.Repo;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoCli.Core.Cli.Plugin;

[Description("Debug builds only: regenerate the Claude Code plugin marketplace and skill in the repository.")]
public sealed class GeneratePluginCommand<TPlugin>(IAnsiConsole console) : Command<GlobalSettings>
    where TPlugin : IPluginSource
{
    public override int Execute(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        var root = RepoLocator.FindRoot(settings.Root, Environment.CurrentDirectory);
        var files = TPlugin.Generate(root);
        PluginGenerator.Write(root, files);

        foreach (var file in files)
            CliOutput.WriteUnwrappedLine(console, $"Wrote [bold]{Markup.Escape(file.Path)}[/]");

        return ExitCodes.Success;
    }
}
