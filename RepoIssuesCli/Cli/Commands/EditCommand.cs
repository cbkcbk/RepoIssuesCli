using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Edit an issue with flags, or interactively when no change flags are given.")]
public sealed class EditCommand(IAnsiConsole console) : IssueCommand<EditCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id.")]
        public string? Reference { get; init; }

        [CommandOption("-n|--name <NAME>")]
        public string? Name { get; init; }

        [CommandOption("-d|--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("--clear-description")]
        public bool ClearDescription { get; init; }

        [CommandOption("-p|--priority <PRIORITY>")]
        [TypeConverter(typeof(EnumOptionConverter<IssuePriority>))]
        public IssuePriority? Priority { get; init; }

        [CommandOption("-s|--status <STATUS>")]
        [TypeConverter(typeof(EnumOptionConverter<IssueStatus>))]
        public IssueStatus? Status { get; init; }

        [CommandOption("--add-tag <TAG>")]
        public string[] AddTags { get; init; } = [];

        [CommandOption("--remove-tag <TAG>")]
        public string[] RemoveTags { get; init; } = [];

        [CommandOption("--add-blocker <REF>")]
        public string[] AddBlockers { get; init; } = [];

        [CommandOption("--remove-blocker <REF>")]
        public string[] RemoveBlockers { get; init; } = [];

        [CommandOption("--add-location <PATH:LINE:COL>")]
        public string[] AddLocations { get; init; } = [];

        [CommandOption("--remove-location <PATH[:LINE[:COL]]>")]
        [Description("Removes an exact location, or every location in a file when only a path is given.")]
        public string[] RemoveLocations { get; init; } = [];

        public bool HasChanges =>
            Name is not null || Description is not null || ClearDescription || Priority is not null || Status is not null
            || AddTags.Length > 0 || RemoveTags.Length > 0 || AddBlockers.Length > 0 || RemoveBlockers.Length > 0
            || AddLocations.Length > 0 || RemoveLocations.Length > 0;

        public override ValidationResult Validate() =>
            Description is not null && ClearDescription
                ? ValidationResult.Error("--description and --clear-description cannot be combined.")
                : ValidationResult.Success();
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var issues = session.Issues;
        var original = session.Interaction.ChooseIssue(issues, settings.Reference, "Edit which issue?");

        Issue? edited;
        if (settings.HasChanges)
            edited = ApplyFlags(original, settings, issues, session.Store.Root);
        else if (session.Interaction.IsInteractive)
            edited = EditInteractively(original, session);
        else
            throw new UsageException("No changes given. Pass change options such as --name or --status, or run interactively.");

        if (edited is null)
        {
            Console.MarkupLine("[grey]Cancelled.[/]");
            return ExitCodes.Success;
        }

        IssueInput.EnsureNoCycle(issues, original, edited.BlockedBy);
        var changed = IssueJson.Serialize(edited) != IssueJson.Serialize(original);
        var saved = changed ? edited with { Updated = Issue.Timestamp() } : original;
        if (changed)
            session.Store.Save(saved);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, saved);
        else
            IssueRenderer.WriteSaved(Console, changed ? "Updated" : "Unchanged", saved);

        return ExitCodes.Success;
    }

    static Issue ApplyFlags(Issue issue, Settings settings, IReadOnlyList<Issue> issues, string repoRoot)
    {
        var removedTags = IssueInput.ParseTags(settings.RemoveTags);
        var tags = issue.Tags
            .Where(tag => !removedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Concat(IssueInput.ParseTags(settings.AddTags));

        var removedBlockers = settings.RemoveBlockers.Select(reference => IssueResolver.ResolveNumber(issues, reference)).ToHashSet();
        var blockers = issue.BlockedBy
            .Where(number => !removedBlockers.Contains(number))
            .Concat(IssueInput.ResolveBlockers(issues, settings.AddBlockers, issue.Number));

        var removedLocations = settings.RemoveLocations.Select(value => IssueInput.ParseLocation(value, repoRoot)).ToList();
        var locations = issue.Locations
            .Where(location => !removedLocations.Any(removed => Covers(removed, location)))
            .Concat(IssueInput.ParseLocations(settings.AddLocations, repoRoot));

        return issue with
        {
            Name = string.IsNullOrWhiteSpace(settings.Name) ? issue.Name : settings.Name.Trim(),
            Description = settings.ClearDescription ? null : settings.Description ?? issue.Description,
            Priority = settings.Priority ?? issue.Priority,
            Status = settings.Status ?? issue.Status,
            Tags = Distinct(tags, StringComparer.OrdinalIgnoreCase),
            BlockedBy = Distinct(blockers),
            Locations = Distinct(locations),
        };
    }

    Issue? EditInteractively(Issue issue, IssueSession session)
    {
        var interaction = session.Interaction;
        var others = session.Issues.Where(candidate => candidate.Id != issue.Id).ToList();

        while (true)
        {
            IssueRenderer.WriteDetails(Console, issue, session.Issues);
            var field = interaction.Choose("Edit:", Enum.GetValues<EditField>(), FieldLabel, "<field>");
            switch (field)
            {
                case EditField.Save:
                    return issue;
                case EditField.Cancel:
                    return null;
                case EditField.Name:
                    issue = issue with { Name = interaction.AskOptionalText("Name:", issue.Name) ?? issue.Name };
                    break;
                case EditField.Description:
                    issue = issue with { Description = interaction.AskOptionalText("Description [grey](empty clears)[/]:", issue.Description) };
                    break;
                case EditField.Status:
                    issue = issue with { Status = interaction.ChooseEnum("Status:", issue.Status) };
                    break;
                case EditField.Priority:
                    issue = issue with { Priority = interaction.ChooseEnum("Priority:", issue.Priority) };
                    break;
                case EditField.Tags:
                    var tagText = interaction.AskOptionalText("Tags [grey](comma-separated)[/]:", string.Join(", ", issue.Tags));
                    issue = issue with { Tags = IssueInput.ParseTags([tagText ?? ""]) };
                    break;
                case EditField.Blockers:
                    var current = others.Where(other => issue.BlockedBy.Contains(other.Number));
                    var chosen = interaction.ChooseMany("Blocked by:", others, current, other => other.Title);
                    var candidate = issue with { BlockedBy = chosen.Select(other => other.Number).ToList() };
                    try
                    {
                        IssueInput.EnsureNoCycle(session.Issues, issue, candidate.BlockedBy);
                        issue = candidate;
                    }
                    catch (UsageException exception)
                    {
                        Console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
                    }
                    break;
                case EditField.Locations:
                    issue = issue with { Locations = EditLocations(issue, interaction, session.Store.Root) };
                    break;
            }
        }
    }

    IReadOnlyList<SourceLocation> EditLocations(Issue issue, Interaction interaction, string repoRoot)
    {
        var kept = interaction.ChooseMany("Keep locations:", issue.Locations, issue.Locations, location => location.ToString()).ToList();
        while (interaction.AskOptionalText("Add location [grey](path:line:col, empty to finish)[/]:") is { } text)
        {
            try
            {
                kept.Add(IssueInput.ParseLocation(text, repoRoot));
            }
            catch (UsageException exception)
            {
                Console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            }
        }

        return Distinct(kept);
    }

    static IReadOnlyList<T> Distinct<T>(IEnumerable<T> values, IEqualityComparer<T>? comparer = null) => values.Distinct(comparer).ToList();

    static bool Covers(SourceLocation removed, SourceLocation location) =>
        removed.Line is null ? removed.Path == location.Path : removed == location;

    static string FieldLabel(EditField field) => field switch
    {
        EditField.Blockers => "Blocked by",
        _ => field.ToString(),
    };

    enum EditField
    {
        Name,
        Description,
        Status,
        Priority,
        Tags,
        Blockers,
        Locations,
        Save,
        Cancel,
    }
}
