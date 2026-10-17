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
            IssueRenderer.WriteJson(console, new Dictionary<string, string> { ["folder"] = store.Folder, ["created"] = created.ToString().ToLowerInvariant() });
        else if (created)
            console.MarkupLine($"Initialized [bold]{Markup.Escape(store.Folder)}[/]");
        else
            console.MarkupLine($"[grey]Already initialized:[/] {Markup.Escape(store.Folder)}");

        return ExitCodes.Success;
    }
}
