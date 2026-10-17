using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Create an issue. Prompts for anything missing when run interactively without a name.")]
public sealed class NewCommand(IAnsiConsole console) : IssueCommand<NewCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("Issue name.")]
        public string? Name { get; init; }

        [CommandOption("-d|--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("-a|--author <AUTHOR>")]
        [Description("Author. Defaults to the git author (user.name <user.email>).")]
        public string? Author { get; init; }

        [CommandOption("-p|--priority <PRIORITY>")]
        [Description("Low, Medium (default), High or Critical.")]
        [TypeConverter(typeof(EnumOptionConverter<IssuePriority>))]
        public IssuePriority? Priority { get; init; }

        [CommandOption("-s|--status <STATUS>")]
        [Description("Open (default), InProgress, Done or Cancelled.")]
        [TypeConverter(typeof(EnumOptionConverter<IssueStatus>))]
        public IssueStatus? Status { get; init; }

        [CommandOption("-t|--tag <TAG>")]
        [Description("Tag; repeatable or comma-separated.")]
        public string[] Tags { get; init; } = [];

        [CommandOption("-b|--blocked-by <REF>")]
        [Description("Blocking issue (number, name or id); repeatable.")]
        public string[] BlockedBy { get; init; } = [];

        [CommandOption("-l|--location <PATH:LINE:COL>")]
        [Description("Source location; repeatable.")]
        public string[] Locations { get; init; } = [];
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var interaction = session.Interaction;
        var issues = session.Issues;
        var wizard = string.IsNullOrWhiteSpace(settings.Name) && interaction.IsInteractive;

        var name = interaction.RequireText(settings.Name, "[name]", "Name:");
        var description = settings.Description;
        var priority = settings.Priority;
        var tags = IssueInput.ParseTags(settings.Tags);
        var blockers = IssueInput.ResolveBlockers(issues, settings.BlockedBy);
        var locations = IssueInput.ParseLocations(settings.Locations, session.Store.Root);

        if (wizard)
        {
            description ??= interaction.AskMultilineText("Description [grey](optional)[/]:");
            priority ??= interaction.ChooseEnum("Priority:", IssuePriority.Medium);
            if (tags.Count == 0)
                tags = IssueInput.ParseTags([interaction.AskOptionalText("Tags [grey](comma-separated, optional)[/]:") ?? ""]);
            if (blockers.Count == 0)
            {
                var open = issues.Where(issue => !issue.Status.IsClosed()).ToList();
                blockers = interaction.ChooseMany("Blocked by:", open, [], issue => issue.Title).Select(issue => issue.Number).ToList();
            }
        }

        var now = Issue.Timestamp();
        var created = new Issue
        {
            Id = Guid.CreateVersion7(),
            Number = session.Snapshot.NextNumber(),
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            Author = string.IsNullOrWhiteSpace(settings.Author) ? GitIdentity.Author(session.Store.Root) : settings.Author.Trim(),
            Status = settings.Status ?? IssueStatus.Open,
            Priority = priority ?? IssuePriority.Medium,
            BlockedBy = blockers,
            Locations = locations,
            Tags = tags,
            Created = now,
            Updated = now,
        };
        session.Store.Save(created);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, created);
        else
            IssueRenderer.WriteSaved(Console, "Created", created);

        return ExitCodes.Success;
    }
}
