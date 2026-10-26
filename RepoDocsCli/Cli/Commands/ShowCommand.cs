using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Show a feature (status, summary, when hints and entry summaries) or one entry in full. Plan entries include progress from .repo-issues. Records the read date of every entry whose body is shown.")]
public sealed class ShowCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<ShowCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<address>")]
        [Description("A feature id, feature/entry, repo (the repo.yaml entries) or repo/entry.")]
        public string Address { get; init; } = "";

        [CommandOption("--full")]
        [Description("For a feature, include every entry's body.")]
        public bool Full { get; init; }
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        RequireInitialized(store);
        var address = DocsAddress.Parse(settings.Address);
        var plans = new PlanLookup(store.Root);

        if (address.Entry is { } entryId)
        {
            MarkRead(store, address.Feature, [entryId]);
            var entry = EntriesOf(store, address.Feature).FirstOrDefault(candidate => candidate.Id == entryId)
                        ?? throw new EntryNotFoundException(address.ToString());
            var view = EntryView.Create(address.Feature, entry, withBody: true, plans);
            ReportPlanIssues(plans);
            if (settings.Json)
                CliOutput.WriteJson(Console, DocsJson.SerializeOutput(view));
            else
                DocsRenderer.WriteEntry(Console, view);
            return ExitCodes.Success;
        }

        if (settings.Full)
            MarkRead(store, address.Feature, EntriesOf(store, address.Feature).Select(entry => entry.Id).ToList());

        var feature = address.IsRepo
            ? FeatureView.Create(LoadRepo(store), settings.Full, plans)
            : FeatureView.Create(store.LoadFeature(address.Feature), settings.Full, plans);
        ReportPlanIssues(plans);
        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(feature));
        else
            DocsRenderer.WriteFeature(Console, feature);
        return ExitCodes.Success;
    }

    static void MarkRead(DocsStore store, string owner, IReadOnlyList<string> entryIds)
    {
        if (entryIds.Count == 0)
            return;

        using var lease = store.Lock();
        foreach (var entryId in entryIds)
            store.MarkRead(owner, entryId);
    }

    static IReadOnlyList<FeatureEntry> EntriesOf(DocsStore store, string owner) =>
        owner == FeatureId.RepoFileStem ? LoadRepo(store).Entries : store.LoadFeature(owner).Entries;

    static RepoDocs LoadRepo(DocsStore store)
    {
        var snapshot = store.Load();
        var repoFile = DocsStore.FileNameFor(FeatureId.RepoFileStem);
        return snapshot.Errors.FirstOrDefault(error => error.FileName == repoFile) is { } error
            ? throw new DocsFileException(error)
            : snapshot.Repo;
    }
}