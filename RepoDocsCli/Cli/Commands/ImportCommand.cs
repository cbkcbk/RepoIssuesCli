using System.ComponentModel;
using RepoDocsCli.Features.ClaudeMd;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Import;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Import the sections of existing CLAUDE.md and AGENTS.md files as entries, guessing feature and kind. Source files are left unchanged.")]
public sealed class ImportCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<ImportCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[files]")]
        [Description("Markdown files to import. Defaults to the root CLAUDE.md and AGENTS.md plus nested ones.")]
        public string[] Files { get; init; } = [];

        [CommandOption("--dry-run")]
        [Description("Print the proposed entries without writing anything.")]
        public bool DryRun { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        if (!store.IsInitialized)
            throw new UsageException($"No {RepoLocator.DocsFolderName} folder in {store.Root}; run 'docs init' first.");

        var sources = ImportSources.Resolve(store.Root, settings.Files, Environment.CurrentDirectory);
        var result = settings.DryRun ? Plan(store, sources, dryRun: true).Result : Write(store, sources);

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(result));
        else
            Report(result);

        return ExitCodes.Success;
    }

    static ImportResult Write(DocsStore store, IReadOnlyList<string> sources)
    {
        using var lease = store.Lock();
        var plan = Plan(store, sources, dryRun: false);
        plan.Save(store);
        return plan.Result;
    }

    static ImportPlan Plan(DocsStore store, IReadOnlyList<string> sources, bool dryRun)
    {
        var snapshot = store.Load();
        if (snapshot.Errors is [var error, ..])
            throw new DocsFileException(error with { Message = $"{error.Message} Fix it before importing." });

        return new DocsImporter(store.Root, snapshot, DocsTime.Now(store.Clock)).Plan(sources, dryRun);
    }

    void Report(ImportResult result)
    {
        if (result.Sources.Count == 0)
        {
            CliOutput.WriteUnwrappedLine(Console, $"[grey]No {string.Join(" or ", ImportSources.FileNames)} files found.[/]");
            return;
        }

        var verb = result.DryRun ? "Would import" : "Imported";
        CliOutput.WriteUnwrappedLine(Console, $"{verb} {Count(result.Imported.Count, "entry", "entries")} from {Count(result.Sources.Count, "file", "files")}{(result.Imported.Count > 0 ? ":" : ".")}");
        foreach (var entry in result.Imported)
            WriteEntry(entry);

        if (result.CreatedFeatures.Count > 0)
            CliOutput.WriteUnwrappedLine(Console, $"{(result.DryRun ? "Would create" : "Created")} features: [bold]{Markup.Escape(string.Join(", ", result.CreatedFeatures))}[/]");

        if (result.Skipped.Count > 0)
        {
            CliOutput.WriteUnwrappedLine(Console, $"Skipped {Count(result.Skipped.Count, "entry", "entries")} already present:");
            foreach (var entry in result.Skipped)
                WriteEntry(entry);
        }

        if (result.Imports.Count > 0)
        {
            CliOutput.WriteUnwrappedLine(Console, "Left @import lines in place:");
            foreach (var reference in result.Imports)
                CliOutput.WriteUnwrappedLine(Console, $"  {Markup.Escape(reference.Text)} [grey]({Markup.Escape(reference.Source)}:{reference.Line})[/]");
        }

        CliOutput.WriteUnwrappedLine(Console, result.DryRun
            ? "[grey]Nothing written; run without --dry-run to import.[/]"
            : $"Review the imported entries, then remove the imported text from the source files and run [bold]docs render[/] to update {ClaudeMdFile.FileName}.");
    }

    void WriteEntry(ImportedEntry entry) =>
        CliOutput.WriteUnwrappedLine(Console,
            $"  [bold]{Markup.Escape(entry.Address)}[/] [grey]{Markup.Escape(entry.Kind)}[/] {Markup.Escape(entry.Summary)} [grey]({Markup.Escape(entry.Source)}:{entry.Line})[/]");

    static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
