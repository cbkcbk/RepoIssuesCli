using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Create a feature (docs add billing) or an entry (docs add billing/rounding --kind rule --summary ...). Prompts for anything missing when run interactively without a target.")]
public sealed class AddCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<AddCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[target]")]
        [Description("A feature id to create a feature, or feature/entry (repo/entry for repo-wide entries) to create an entry.")]
        public string? Target { get; init; }

        [CommandOption("-e|--entry")]
        [Description("Create an entry in the feature given as target, with an id derived from the summary.")]
        public bool Entry { get; init; }

        [CommandOption("-k|--kind <KIND>")]
        [Description("Entry kind, declared in config.yaml (rule, fact, gotcha, howto, decision, plan by default).")]
        public string? Kind { get; init; }

        [CommandOption("-s|--summary <TEXT>")]
        [Description("One-line summary. Required for entries.")]
        public string? Summary { get; init; }

        [CommandOption("--status <STATUS>")]
        [Description("Feature status: proposed (default), planned, in-progress, shipped or deprecated.")]
        [TypeConverter(typeof(EnumOptionConverter<FeatureStatus>))]
        public FeatureStatus? Status { get; init; }

        [CommandOption("-b|--body <TEXT>")]
        [Description("Entry body (markdown).")]
        public string? Body { get; init; }

        [CommandOption("--body-file <PATH>")]
        [Description("Read the entry body from a file.")]
        public string? BodyFile { get; init; }

        [CommandOption("--body-stdin")]
        [Description("Read the entry body from stdin.")]
        public bool BodyStdin { get; init; }

        [CommandOption("-p|--path <PATH>")]
        [Description("Path hint (file, or folder ending in /); repeatable.")]
        public string[] Paths { get; init; } = [];

        [CommandOption("-a|--anchor <PATH:LINE[-END]>")]
        [Description("Anchor hint on a line or line range of a file; repeatable.")]
        public string[] Anchors { get; init; } = [];

        [CommandOption("-w|--keyword <WORD>")]
        [Description("Keyword hint; repeatable or comma-separated.")]
        public string[] Keywords { get; init; } = [];

        [CommandOption("--step <TITLE>")]
        [Description("Plan step, with linked issues as trailing #n tokens (\"Schema and store #23 #24\"); repeatable. Plan entries only.")]
        public string[] Steps { get; init; } = [];

        public bool HasEntryOptions => Kind is not null || Body is not null || BodyFile is not null || BodyStdin || Steps.Length > 0;
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        DocsInput.RequireInitialized(store);
        var interaction = new Interaction(Console, settings);
        var wizard = string.IsNullOrWhiteSpace(settings.Target) && interaction.IsInteractive;
        var target = DocsAddress.Parse(interaction.RequireText(settings.Target, "[target]", "Feature, or feature/entry:"));

        if (settings.Entry && target.IsEntry)
            throw new UsageException("--entry derives the entry id from the summary; pass only the feature, or drop --entry.");

        var result = target.IsEntry || settings.Entry
            ? AddEntry(store, settings, interaction, wizard, target.Feature, target.Entry)
            : AddFeature(store, settings, interaction, wizard, target.Feature);

        DocsItemOutput.Write(Console, settings.Json, "Created", result);
        return ExitCodes.Success;
    }

    static DocsItemResult AddFeature(DocsStore store, Settings settings, Interaction interaction, bool wizard, string id)
    {
        if (settings.HasEntryOptions)
            throw new UsageException("--kind, --body, --body-file, --body-stdin and --step apply to entries; use feature/entry or --entry.");
        FeatureId.Require(id);
        if (store.Exists(id))
            throw new UsageException($"Feature '{id}' already exists.");

        var summary = settings.Summary;
        var status = settings.Status;
        if (wizard)
        {
            summary ??= interaction.AskOptionalText("Summary [grey](optional)[/]:");
            status ??= interaction.ChooseEnum("Status:", FeatureStatus.Proposed);
        }

        var feature = new Feature
        {
            Id = id,
            Status = status ?? FeatureStatus.Proposed,
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
            When = new DocsInput(store.Root).Hints(settings.Paths, settings.Anchors, settings.Keywords),
        };

        using var lease = store.Lock();
        if (store.Exists(id))
            throw new UsageException($"Feature '{id}' already exists.");

        var snapshot = store.Load();
        DocsChange.EnsureValid(snapshot, DocsChange.WithFeature(snapshot, null, feature));
        return DocsItemResult.For(store.Save(feature));
    }

    static DocsItemResult AddEntry(DocsStore store, Settings settings, Interaction interaction, bool wizard, string ownerId, string? entryId)
    {
        if (settings.Status is not null)
            throw new UsageException("--status applies to features, not entries.");
        if (entryId is not null && !EntryId.IsValid(entryId))
            throw new UsageException($"'{entryId}' is not a valid entry id. Use lowercase letters, digits and single hyphens.");

        var config = store.Load().Config;
        var owner = store.LoadOwner(ownerId);
        EnsureNew(owner, entryId);

        var kind = settings.Kind?.Trim() ?? interaction.Choose("Kind:", config.Kinds.Keys.ToList(), name => name, "--kind");
        DocsInput.RequireKind(config, kind);
        var summary = interaction.RequireText(settings.Summary, "--summary", "Summary:");
        var body = DocsInput.ReadBody(settings.Body, settings.BodyFile, settings.BodyStdin);
        if (wizard)
            body ??= DocsInput.NormalizeBody(interaction.AskMultilineText("Body [grey](optional)[/]:"));

        var hints = new DocsInput(store.Root).Hints(settings.Paths, settings.Anchors, settings.Keywords);
        var steps = settings.Steps.Select(DocsInput.ParseStep).ToList();

        using var lease = store.Lock();
        owner = store.LoadOwner(ownerId);
        EnsureNew(owner, entryId);

        var entry = new FeatureEntry
        {
            Id = entryId ?? EntryId.FromSummary(summary, owner.Entries.Select(existing => existing.Id)),
            Kind = kind,
            Summary = summary,
            When = hints.HasHints() ? hints : null,
            Steps = steps,
            Body = body,
        };
        var changed = owner.WithEntries([.. owner.Entries, entry]);

        var snapshot = store.Load();
        DocsChange.EnsureValid(snapshot, changed.ApplyTo(snapshot));
        return DocsItemResult.For(owner.Id, store.Save(changed).Require(entry.Id));
    }

    static void EnsureNew(EntryOwner owner, string? entryId)
    {
        if (entryId is not null && owner.Find(entryId) is not null)
            throw new UsageException($"Entry '{EntryId.Address(owner.Id, entryId)}' already exists.");
    }
}
