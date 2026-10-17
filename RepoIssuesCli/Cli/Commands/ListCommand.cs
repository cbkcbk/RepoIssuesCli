using System.ComponentModel;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("List issues. Hides Done and Cancelled unless --all or --status is given.")]
public sealed class ListCommand(IAnsiConsole console) : IssueCommand<ListCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("-s|--status <STATUS>")]
        [Description("Only these statuses; repeatable.")]
        [TypeConverter(typeof(EnumOptionConverter<IssueStatus>))]
        public IssueStatus[] Statuses { get; init; } = [];

        [CommandOption("-p|--priority <PRIORITY>")]
        [Description("Only these priorities; repeatable.")]
        [TypeConverter(typeof(EnumOptionConverter<IssuePriority>))]
        public IssuePriority[] Priorities { get; init; } = [];

        [CommandOption("-t|--tag <TAG>")]
        [Description("Only issues with all these tags; repeatable.")]
        public string[] Tags { get; init; } = [];

        [CommandOption("--blocked")]
        [Description("Only issues with an unfinished blocker.")]
        public bool Blocked { get; init; }

        [CommandOption("--ready")]
        [Description("Only issues without unfinished blockers.")]
        public bool Ready { get; init; }

        [CommandOption("-a|--all")]
        [Description("Include Done and Cancelled issues.")]
        public bool All { get; init; }

        public override ValidationResult Validate() =>
            Blocked && Ready ? ValidationResult.Error("--blocked and --ready cannot be combined.") : ValidationResult.Success();
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var issues = session.Issues;
        var tags = IssueInput.ParseTags(settings.Tags);

        var shown = issues
            .Where(issue => settings.Statuses.Length > 0 ? settings.Statuses.Contains(issue.Status) : settings.All || !issue.Status.IsClosed())
            .Where(issue => settings.Priorities.Length == 0 || settings.Priorities.Contains(issue.Priority))
            .Where(issue => tags.All(tag => issue.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .Where(issue => !settings.Blocked || BlockerGraph.IsBlocked(issue, issues))
            .Where(issue => !settings.Ready || !BlockerGraph.IsBlocked(issue, issues))
            .OrderByDescending(issue => issue.Priority)
            .ThenBy(issue => issue.Number)
            .ToList();

        if (settings.Json)
            IssueRenderer.WriteJson(Console, shown);
        else
            IssueRenderer.WriteList(Console, shown, issues);

        return ExitCodes.Success;
    }
}
