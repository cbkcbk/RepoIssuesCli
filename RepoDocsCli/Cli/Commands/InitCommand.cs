using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Create the .repo-docs folder at the repository root, with one feature per Features/{X}/ folder.")]
public sealed class InitCommand(IAnsiConsole console) : Command<GlobalSettings>
{
    public override int Execute(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        var store = DocsStore.Open(settings.Root);
        var result = store.Initialize();

        if (settings.Json)
        {
            CliOutput.WriteJson(console, DocsJson.SerializeOutput(result));
            return ExitCodes.Success;
        }

        CliOutput.WriteUnwrappedLine(console, result.Created
            ? $"Initialized [bold]{Markup.Escape(result.Folder)}[/]"
            : $"[grey]Already initialized:[/] {Markup.Escape(result.Folder)}");
        foreach (var feature in result.Features)
            CliOutput.WriteUnwrappedLine(console, $"Added feature [bold]{Markup.Escape(feature.Id)}[/] [grey]({Markup.Escape(string.Join(", ", feature.When.Paths))})[/]");

        return ExitCodes.Success;
    }
}
