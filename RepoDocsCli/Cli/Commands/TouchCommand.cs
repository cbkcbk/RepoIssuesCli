using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Mark a feature or entry as updated now, or, without a target, stamp entries edited by hand since the last commit.")]
public sealed class TouchCommand(IAnsiConsole console) : Command<TouchCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[target]")]
        [Description("A feature id, feature/entry or repo/entry. Omit to stamp hand edits.")]
        public string? Target { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var store = DocsStore.Open(settings.Root);
        if (!store.IsInitialized)
            throw new UsageException("No .repo-docs folder; run 'docs init' first.");

        using var lease = store.Lock();
        var result = settings.Target is { } target ? TouchTarget(store, target) : new TouchResult(null, null, store.StampHandEdits());

        if (settings.Json)
            CliOutput.WriteJson(console, DocsJson.SerializeOutput(result));
        else if (result.Target is { } touched)
            CliOutput.WriteUnwrappedLine(console, $"Touched [bold]{Markup.Escape(touched)}[/] [grey]({result.Updated?.ToString(DocsTime.TimestampFormat)})[/]");
        else if (result.Files.Count == 0)
            CliOutput.WriteUnwrappedLine(console, "[grey]No hand edits to stamp.[/]");
        else
            foreach (var file in result.Files)
                CliOutput.WriteUnwrappedLine(console, $"Stamped [bold]{Markup.Escape(file)}[/]");

        return ExitCodes.Success;
    }

    static TouchResult TouchTarget(DocsStore store, string target)
    {
        var parts = target.Split(EntryId.Separator);
        if (parts.Length > 2 || parts.Any(string.IsNullOrEmpty))
            throw new UsageException($"'{target}' is not a feature id, feature/entry or repo/entry.");

        var featureId = parts[0];
        var updated = store.Touch(featureId, parts.ElementAtOrDefault(1));
        var file = Path.GetRelativePath(store.Root, featureId == FeatureId.RepoFileStem ? store.RepoFile : store.PathFor(featureId)).Replace('\\', '/');
        return new TouchResult(target, updated, [file]);
    }
}
