using System.ComponentModel;
using RepoDocsCli.Features.Affected;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("List docs whose anchors or paths changed since a git ref (default HEAD: uncommitted changes) but that were not updated. Review each, then docs edit or docs touch it. Writes nothing.")]
public sealed class AffectedCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<AffectedCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--since <REF>")]
        [Description("Compare the working tree (staged, unstaged and untracked files) with this git ref. Defaults to HEAD; use e.g. origin/main in CI.")]
        public string Since { get; init; } = GitDiff.WorkingHead;

        [CommandOption("--strict")]
        [Description("Exit with 1 when any docs are affected.")]
        public bool Strict { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        RequireInitialized(store);
        var result = DocsAffected.Find(store.Root, LoadReportingErrors(store), settings.Since);

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(result));
        else
            Write(result);

        return settings.Strict && result.Entries.Count > 0 ? ExitCodes.ValidationFailed : ExitCodes.Success;
    }

    void Write(AffectedResult result)
    {
        if (result.Entries.Count == 0)
        {
            CliOutput.WriteUnwrappedLine(Console, $"[grey]No docs affected since {Markup.Escape(result.Since)}.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Address").NoWrap())
            .AddColumn(new TableColumn("Kind").NoWrap())
            .AddColumn(new TableColumn("Changed"))
            .AddColumn(new TableColumn("Reason").NoWrap());
        foreach (var entry in result.Entries)
            table.AddRow(
                new Markup($"[bold]{Markup.Escape(entry.Address)}[/]"),
                new Text(entry.Kind),
                new Text(string.Join('\n', entry.Matched.Select(Location))),
                new Text(string.Join('\n', entry.Matched.Select(match => match.Reason.ToString()))));
        Console.Write(table);

        CliOutput.WriteUnwrappedLine(Console,
            $"{result.Entries.Count} affected since {Markup.Escape(result.Since)}. Review each, then run [bold]docs edit <address>[/], or [bold]docs touch <address>[/] if it is still correct.");
    }

    static string Location(AffectedMatch match) =>
        (match.Anchor ?? match.Path) + (match.PreviousPath is { } previous ? $" (was {previous})" : "");
}
