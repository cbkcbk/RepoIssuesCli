using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Create .repo-infra with a starter infra.luau, the @infra type stubs and .luaurc. Rerun to refresh the stubs; an existing infra.luau is never overwritten.")]
public sealed class InitCommand(IAnsiConsole console, ErrorConsole errors) : InfraCommand<InfraSettings>(console, errors)
{
    protected override int Run(InfraWorkspace workspace, InfraSettings settings, InfraLog log)
    {
        var result = InfraScaffold.Initialize(workspace);
        log.Info("init.done", result.Created ? $"Initialized {result.Folder}" : $"Refreshed {result.Folder}", new Dictionary<string, object?>
        {
            ["created"] = result.Created,
            ["files"] = result.Files.Count,
            ["stubs"] = result.Stubs.Count,
        });

        if (settings.Json)
        {
            CliOutput.WriteJson(Console, InfraJson.SerializeOutput(result));
            return InfraExitCodes.Success;
        }

        CliOutput.WriteUnwrappedLine(Console, result.Created
            ? $"Initialized [bold]{Markup.Escape(result.Folder)}[/] for project [bold]{Markup.Escape(result.Project)}[/]"
            : $"[grey]Already initialized:[/] {Markup.Escape(result.Folder)}");
        foreach (var file in result.Files)
            CliOutput.WriteUnwrappedLine(Console, $"Created {Markup.Escape(file)}");
        if (result.Stubs.Count > 0)
            CliOutput.WriteUnwrappedLine(Console, $"Updated {result.Stubs.Count} @infra stub file(s) in {Markup.Escape(workspace.RelativePath(workspace.StubsFolder))}");

        return InfraExitCodes.Success;
    }
}
