using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Create the .repo-issues folder at the repository root.")]
public sealed class InitCommand(IAnsiConsole console) : Command<GlobalSettings>
{
    public override int Execute(CommandContext context, GlobalSettings settings, CancellationToken cancellationToken)
    {
        var store = IssueStore.Open(settings.Root);
        var created = store.Initialize();

        if (settings.Json)
            IssueRenderer.WriteJson(console, new InitResult(store.Folder, created));
        else if (created)
            CliOutput.WriteUnwrappedLine(console, $"Initialized [bold]{Markup.Escape(store.Folder)}[/]");
        else
            CliOutput.WriteUnwrappedLine(console, $"[grey]Already initialized:[/] {Markup.Escape(store.Folder)}");

        return ExitCodes.Success;
    }
}