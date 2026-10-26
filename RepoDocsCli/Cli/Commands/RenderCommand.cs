using System.ComponentModel;
using RepoDocsCli.Features.ClaudeMd;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Write the generated block into the repo-root CLAUDE.md: repo.yaml entries, one line per feature and the entries whose kind renders. Text outside the block is kept.")]
public sealed class RenderCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<RenderCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--check")]
        [Description("Write nothing; exit with 1 when the block in CLAUDE.md is missing or stale.")]
        public bool Check { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        if (!store.IsInitialized)
            throw new UsageException($"No {RepoLocator.DocsFolderName} folder in {store.Root}; run 'docs init' first.");

        var result = settings.Check ? ClaudeMdFile.Check(store.Root, LoadReportingErrors(store)) : Write(store);

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(result));
        else
            CliOutput.WriteUnwrappedLine(Console, Message(result, settings.Check));

        return settings.Check && result.Stale ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }

    RenderResult Write(DocsStore store)
    {
        using var lease = store.Lock();
        return ClaudeMdFile.Write(store.Root, LoadReportingErrors(store));
    }

    static string Message(RenderResult result, bool check) => result switch
    {
        { Stale: true } when check => $"[red]{ClaudeMdFile.FileName} is stale;[/] run [bold]docs render[/].",
        { Created: true } => $"Created [bold]{Markup.Escape(result.Path)}[/]",
        { Changed: true } => $"Updated [bold]{Markup.Escape(result.Path)}[/]",
        _ => $"[grey]Up to date:[/] {Markup.Escape(result.Path)}",
    };
}
