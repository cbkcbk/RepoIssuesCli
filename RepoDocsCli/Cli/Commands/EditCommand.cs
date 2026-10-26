using System.ComponentModel;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("Change a feature or entry with flags, or interactively when no change flags are given. --id renames it.")]
public sealed class EditCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<EditCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[target]")]
        [Description("A feature id, feature/entry, repo/entry, or repo for the repo.yaml summary.")]
        public string? Target { get; init; }

        [CommandOption("--id <ID>")]
        [Description("New id. A feature is renamed with git mv; an entry is renamed in its file.")]
        public string? Id { get; init; }

        [CommandOption("-s|--summary <TEXT>")]
        [Description("One-line summary.")]
        public string? Summary { get; init; }

        [CommandOption("--clear-summary")]
        [Description("Remove the summary of a feature or repo.yaml.")]
        public bool ClearSummary { get; init; }

        [CommandOption("--status <STATUS>")]
        [Description("Feature status: proposed, planned, in-progress, shipped or deprecated.")]
        [TypeConverter(typeof(EnumOptionConverter<FeatureStatus>))]
        public FeatureStatus? Status { get; init; }

        [CommandOption("-k|--kind <KIND>")]
        [Description("Entry kind, declared in config.yaml.")]
        public string? Kind { get; init; }

        [CommandOption("-b|--body <TEXT>")]
        [Description("Replace the entry body (markdown).")]
        public string? Body { get; init; }

        [CommandOption("--body-file <PATH>")]
        [Description("Replace the entry body with a file's text.")]
        public string? BodyFile { get; init; }

        [CommandOption("--body-stdin")]
        [Description("Replace the entry body with stdin.")]
        public bool BodyStdin { get; init; }

        [CommandOption("--clear-body")]
        public bool ClearBody { get; init; }

        [CommandOption("--add-path <PATH>")]
        public string[] AddPaths { get; init; } = [];

        [CommandOption("--remove-path <PATH>")]
        public string[] RemovePaths { get; init; } = [];

        [CommandOption("--add-anchor <PATH:LINE[-END]>")]
        [Description("Anchor a line or line range; an existing anchor on the same path and line is replaced.")]
        public string[] AddAnchors { get; init; } = [];

        [CommandOption("--remove-anchor <ANCHOR>")]
        [Description("Removes anchors by path, path:line or symbol.")]
        public string[] RemoveAnchors { get; init; } = [];

        [CommandOption("--add-keyword <WORD>")]
        public string[] AddKeywords { get; init; } = [];

        [CommandOption("--remove-keyword <WORD>")]
        public string[] RemoveKeywords { get; init; } = [];

        [CommandOption("--add-step <TITLE>")]
        [Description("Append a plan step, with linked issues as trailing #n tokens (\"Docs #31\").")]
        public string[] AddSteps { get; init; } = [];

        [CommandOption("--remove-step <N>")]
        [Description("Remove plan step N (1-based, numbered before this edit).")]
        public int[] RemoveSteps { get; init; } = [];

        [CommandOption("--set-step <N=TITLE>")]
        [Description("Replace plan step N, as in 2=\"Schema and store #23 #24\".")]
        public string[] SetSteps { get; init; } = [];

        [CommandOption("--step-issues <N=ISSUES>")]
        [Description("Set the issues of plan step N, as in 2=23,24 (2= clears them).")]
        public string[] StepIssues { get; init; } = [];

        public bool HasHintChanges =>
            AddPaths.Length > 0 || RemovePaths.Length > 0 || AddAnchors.Length > 0 || RemoveAnchors.Length > 0
            || AddKeywords.Length > 0 || RemoveKeywords.Length > 0;

        public bool HasEntryChanges =>
            Kind is not null || Body is not null || BodyFile is not null || BodyStdin || ClearBody
            || AddSteps.Length > 0 || RemoveSteps.Length > 0 || SetSteps.Length > 0 || StepIssues.Length > 0;

        public bool HasChanges => Id is not null || Summary is not null || ClearSummary || Status is not null || HasHintChanges || HasEntryChanges;

        public override ValidationResult Validate() =>
            (Summary is not null && ClearSummary) || (ClearBody && (Body is not null || BodyFile is not null || BodyStdin))
                ? ValidationResult.Error("--summary and --clear-summary, or a body option and --clear-body, cannot be combined.")
                : ValidationResult.Success();
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        DocsInput.RequireInitialized(store);
        var interaction = new Interaction(Console, settings);
        var target = DocsInput.ChooseTarget(interaction, store.Load(), settings.Target, "Edit what?");
        if (!settings.HasChanges && !interaction.IsInteractive)
            throw new UsageException("No changes given. Pass change options such as --summary or --add-path, or run interactively.");

        var prompts = new DocsEditPrompts(Console, interaction, new DocsInput(store.Root));
        var outcome = target switch
        {
            { IsEntry: true } => EditEntry(store, settings, prompts, target),
            { IsRepo: true } => EditRepo(store, settings, interaction),
            _ => EditFeature(store, settings, prompts, target.Feature),
        };

        if (outcome is not var (changed, result))
        {
            Console.MarkupLine("[grey]Cancelled.[/]");
            return ExitCodes.Success;
        }

        DocsItemOutput.Write(Console, settings.Json, changed ? "Updated" : "Unchanged", result);
        return ExitCodes.Success;
    }

    (bool Changed, DocsItemResult Result)? EditFeature(DocsStore store, Settings settings, DocsEditPrompts prompts, string id)
    {
        if (settings.HasEntryChanges)
            throw new UsageException("--kind, the body options and the step options apply to entries, not features.");

        Func<Feature, Feature> change = feature => ApplyFlags(feature, settings, prompts.Input);
        if (!settings.HasChanges)
        {
            if (EditInteractively(store.LoadFeature(id), prompts) is not { } edited)
                return null;
            change = _ => edited;
        }

        using var lease = store.Lock();
        var original = store.LoadFeature(id);
        var changed = change(original);
        if (changed.Id != original.Id)
        {
            FeatureId.Require(changed.Id);
            if (store.Exists(changed.Id))
                throw new UsageException($"Feature '{changed.Id}' already exists.");
        }

        var snapshot = store.Load();
        DocsChange.EnsureValid(snapshot, DocsChange.WithFeature(snapshot, original.Id, changed));
        if (DocsYaml.Serialize(changed) == DocsYaml.Serialize(original))
            return (false, DocsItemResult.For(original));

        if (changed.Id != original.Id)
            store.Rename(original.Id, changed.Id);
        return (true, DocsItemResult.For(store.Save(changed)));
    }

    (bool Changed, DocsItemResult Result)? EditEntry(DocsStore store, Settings settings, DocsEditPrompts prompts, DocsAddress target)
    {
        if (settings.Status is not null || settings.ClearSummary)
            throw new UsageException("--status and --clear-summary apply to features, not entries.");

        var config = store.Load().Config;
        if (settings.Kind is { } kind)
            DocsInput.RequireKind(config, kind.Trim());
        var body = DocsInput.ReadBody(settings.Body, settings.BodyFile, settings.BodyStdin);

        Func<FeatureEntry, FeatureEntry> change = entry => ApplyFlags(entry, settings, body, prompts.Input);
        if (!settings.HasChanges)
        {
            var current = store.LoadOwner(target.Feature).Require(target.Entry!);
            if (EditInteractively(EntryId.Address(target.Feature, current.Id), current, config, prompts) is not { } edited)
                return null;
            change = _ => edited;
        }

        using var lease = store.Lock();
        var owner = store.LoadOwner(target.Feature);
        var original = owner.Require(target.Entry!);
        var changed = change(original);
        if (changed.Id != original.Id)
        {
            if (!EntryId.IsValid(changed.Id))
                throw new UsageException($"'{changed.Id}' is not a valid entry id. Use lowercase letters, digits and single hyphens.");
            if (owner.Find(changed.Id) is not null)
                throw new UsageException($"Entry '{EntryId.Address(owner.Id, changed.Id)}' already exists.");
        }

        var changedOwner = owner.Replace(original.Id, changed);
        var snapshot = store.Load();
        DocsChange.EnsureValid(snapshot, changedOwner.ApplyTo(snapshot));
        if (DocsYaml.Serialize(changed) == DocsYaml.Serialize(original))
            return (false, DocsItemResult.For(owner.Id, original));

        return (true, DocsItemResult.For(owner.Id, store.Save(changedOwner).Require(changed.Id)));
    }

    static (bool Changed, DocsItemResult Result)? EditRepo(DocsStore store, Settings settings, Interaction interaction)
    {
        if (settings.HasChanges && (settings.Id is not null || settings.Status is not null || settings.HasHintChanges || settings.HasEntryChanges))
            throw new UsageException($"{DocsStore.FileNameFor(FeatureId.RepoFileStem)} only has a summary; edit its entries as {EntryId.Address(FeatureId.RepoFileStem, "<entry>")}.");

        var summary = settings.HasChanges
            ? settings.Summary
            : interaction.AskOptionalText("Summary [grey](empty keeps it)[/]:", store.LoadOwner(FeatureId.RepoFileStem).Repo?.Summary);

        using var lease = store.Lock();
        var original = store.LoadOwner(FeatureId.RepoFileStem).Repo ?? new RepoDocs();
        var changed = original with { Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim() };
        if (DocsYaml.Serialize(changed) == DocsYaml.Serialize(original))
            return (false, DocsItemResult.For(original));

        return (true, DocsItemResult.For(store.SaveRepo(changed)));
    }

    static Feature ApplyFlags(Feature feature, Settings settings, DocsInput input) => feature with
    {
        Id = settings.Id?.Trim() ?? feature.Id,
        Summary = settings.ClearSummary || settings.Summary is { } summary && string.IsNullOrWhiteSpace(summary)
            ? null
            : settings.Summary?.Trim() ?? feature.Summary,
        Status = settings.Status ?? feature.Status,
        When = ApplyHints(feature.When, settings, input),
    };

    static FeatureEntry ApplyFlags(FeatureEntry entry, Settings settings, string? body, DocsInput input)
    {
        var when = ApplyHints(entry.When ?? new WhenHints(), settings, input);
        return entry with
        {
            Id = settings.Id?.Trim() ?? entry.Id,
            Kind = settings.Kind?.Trim() ?? entry.Kind,
            Summary = settings.Summary?.Trim() ?? entry.Summary,
            Body = settings.ClearBody ? null : body ?? entry.Body,
            When = when.HasHints() ? when : null,
            Steps = ApplySteps(entry.Steps, settings),
        };
    }

    static WhenHints ApplyHints(WhenHints hints, Settings settings, DocsInput input)
    {
        var paths = Remove(hints.Paths, settings.RemovePaths, input.PathMatches, "path hint");
        var anchors = Remove(hints.Anchors, settings.RemoveAnchors, input.AnchorMatches, "anchor");
        var keywords = Remove(hints.Keywords, settings.RemoveKeywords, (keyword, value) => keyword.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase), "keyword");

        var addedAnchors = settings.AddAnchors.Select(input.ParseAnchor).ToList();
        anchors.RemoveAll(anchor => addedAnchors.Any(added => added.Path == anchor.Path && added.Line == anchor.Line));

        return new WhenHints
        {
            Paths = paths.Concat(settings.AddPaths.Select(input.ParsePath)).Distinct().ToList(),
            Anchors = anchors.Concat(addedAnchors).Distinct().ToList(),
            Keywords = keywords.Concat(DocsInput.ParseKeywords(settings.AddKeywords)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    static List<T> Remove<T>(List<T> items, string[] values, Func<T, string, bool> matches, string label)
    {
        var kept = items.ToList();
        foreach (var value in values)
        {
            if (kept.RemoveAll(item => matches(item, value)) == 0)
                throw new UsageException($"No {label} matches '{value}'.");
        }

        return kept;
    }

    static List<PlanStep> ApplySteps(List<PlanStep> current, Settings settings)
    {
        var steps = current.ToList();
        foreach (var value in settings.SetSteps)
        {
            var (index, text) = DocsInput.ParseIndexed(value, "--set-step", steps.Count);
            steps[index] = DocsInput.ParseStep(text);
        }

        foreach (var value in settings.StepIssues)
        {
            var (index, text) = DocsInput.ParseIndexed(value, "--step-issues", steps.Count);
            steps[index] = steps[index] with { Issues = DocsInput.ParseIssues(text) };
        }

        foreach (var number in settings.RemoveSteps.Distinct().OrderDescending())
        {
            if (number < 1 || number > steps.Count)
                throw new UsageException($"--remove-step: there is no step {number}; the plan has {steps.Count} steps.");
            steps.RemoveAt(number - 1);
        }

        steps.AddRange(settings.AddSteps.Select(DocsInput.ParseStep));
        return steps;
    }

    Feature? EditInteractively(Feature feature, DocsEditPrompts prompts)
    {
        var interaction = prompts.Interaction;
        EditField[] fields = [EditField.Id, EditField.Summary, EditField.Status, .. HintFields, EditField.Save, EditField.Cancel];
        while (true)
        {
            DocsItemOutput.WriteDetails(Console, feature.Id, feature);
            var field = interaction.Choose("Edit:", fields, choice => choice.ToString(), "<field>");
            switch (field)
            {
                case EditField.Save:
                    return feature;
                case EditField.Cancel:
                    return null;
                case EditField.Id:
                    feature = feature with { Id = interaction.AskOptionalText("Id:", feature.Id) ?? feature.Id };
                    break;
                case EditField.Summary:
                    feature = feature with { Summary = interaction.AskOptionalText("Summary:", feature.Summary) };
                    break;
                case EditField.Status:
                    feature = feature with { Status = interaction.ChooseEnum("Status:", feature.Status) };
                    break;
                default:
                    feature = feature with { When = EditHints(field, feature.When, prompts) };
                    break;
            }
        }
    }

    FeatureEntry? EditInteractively(string address, FeatureEntry entry, DocsConfig config, DocsEditPrompts prompts)
    {
        var interaction = prompts.Interaction;
        while (true)
        {
            EditField[] fields =
            [
                EditField.Id, EditField.Kind, EditField.Summary, EditField.Body, .. HintFields,
                .. entry.Kind == PlanStep.EntryKind || entry.Steps.Count > 0 ? [EditField.Steps] : Array.Empty<EditField>(),
                EditField.Save, EditField.Cancel,
            ];
            DocsItemOutput.WriteDetails(Console, address, entry);
            var field = interaction.Choose("Edit:", fields, choice => choice.ToString(), "<field>");
            switch (field)
            {
                case EditField.Save:
                    return entry;
                case EditField.Cancel:
                    return null;
                case EditField.Id:
                    entry = entry with { Id = interaction.AskOptionalText("Id:", entry.Id) ?? entry.Id };
                    break;
                case EditField.Kind:
                    entry = entry with { Kind = interaction.Choose("Kind:", config.Kinds.Keys.ToList(), name => name, "--kind") };
                    break;
                case EditField.Summary:
                    entry = entry with { Summary = interaction.AskOptionalText("Summary:", entry.Summary) ?? entry.Summary };
                    break;
                case EditField.Body:
                    entry = entry with { Body = DocsInput.NormalizeBody(interaction.AskMultilineText("Body:", entry.Body)) };
                    break;
                case EditField.Steps:
                    entry = entry with { Steps = prompts.Steps(entry.Steps) };
                    break;
                default:
                    var when = EditHints(field, entry.When ?? new WhenHints(), prompts);
                    entry = entry with { When = when.HasHints() ? when : null };
                    break;
            }
        }
    }

    static readonly EditField[] HintFields = [EditField.Paths, EditField.Anchors, EditField.Keywords];

    static WhenHints EditHints(EditField field, WhenHints hints, DocsEditPrompts prompts) => field switch
    {
        EditField.Paths => prompts.Paths(hints),
        EditField.Anchors => prompts.Anchors(hints),
        EditField.Keywords => prompts.Keywords(hints),
        _ => hints,
    };

    enum EditField
    {
        Id,
        Kind,
        Summary,
        Status,
        Body,
        Paths,
        Anchors,
        Keywords,
        Steps,
        Save,
        Cancel,
    }
}
