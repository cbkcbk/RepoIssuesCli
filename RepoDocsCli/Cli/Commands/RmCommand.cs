using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Remove a feature (its whole file, with git rm) or a single entry.")]
public sealed class RmCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<RmCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[target]")]
        [Description("A feature id, feature/entry or repo/entry.")]
        public string? Target { get; init; }

        [CommandOption("-f|--force")]
        [Description("Remove without confirmation. Required when not interactive.")]
        public bool Force { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        DocsInput.RequireInitialized(store);
        var interaction = new Interaction(Console, settings);
        var target = DocsInput.ChooseTarget(interaction, store.Load(), settings.Target, "Remove what?");
        if (target is { IsRepo: true, IsEntry: false })
            throw new UsageException($"{DocsStore.FileNameFor(FeatureId.RepoFileStem)} cannot be removed; remove its entries as {EntryId.Address(FeatureId.RepoFileStem, "<entry>")}.");

        var found = Find(store, target);
        if (!settings.Force)
        {
            if (!interaction.IsInteractive)
                throw new UsageException("Removing requires --force when not interactive.");
            var what = found.Entry is not null ? "entry" : "feature and all its entries";
            if (!interaction.Confirm($"Remove {what} {Markup.Escape(target.ToString())}?"))
            {
                Console.MarkupLine("[grey]Cancelled.[/]");
                return ExitCodes.Success;
            }
        }

        using (store.Lock())
        {
            found = Find(store, target);
            if (target.Entry is { } entryId)
                store.Save(store.LoadOwner(target.Feature).Replace(entryId, null));
            else
                store.Delete(target.Feature);
        }

        DocsItemOutput.Write(Console, settings.Json, "Removed", found);
        return ExitCodes.Success;
    }

    static DocsItemResult Find(DocsStore store, DocsAddress target) => target.Entry is { } entryId
        ? DocsItemResult.For(target.Feature, store.LoadOwner(target.Feature).Require(entryId))
        : DocsItemResult.For(store.LoadFeature(target.Feature));
}
