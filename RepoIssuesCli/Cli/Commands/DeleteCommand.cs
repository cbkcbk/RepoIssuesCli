using System.ComponentModel;
using RepoCli.Core.Features.IssueLinks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Delete an issue and remove it from other issues' blockers. Warns about .repo-docs plan steps that still link it; 'docs check' reports them.")]
public sealed class DeleteCommand(IAnsiConsole console, ErrorConsole errors) : IssueCommand<DeleteCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id.")]
        public string? Reference { get; init; }

        [CommandOption("-f|--force")]
        [Description("Delete without confirmation. Required when not interactive.")]
        public bool Force { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var interaction = session.Interaction;
        var issues = session.Issues;
        var issue = interaction.ChooseIssue(issues, settings.Reference, "Delete which issue?");
        var numberStillUsed = issues.Any(other => other.Id != issue.Id && other.Number == issue.Number);
        var docs = numberStillUsed ? [] : errors.Report(new DocsSteps(session.Store.Root).Find(issue.Number)).References;

        if (!settings.Force)
        {
            if (!interaction.IsInteractive)
                throw new UsageException("Deleting requires --force when not interactive.");
            if (!interaction.Confirm($"Delete {Markup.Escape(issue.Title)}?{DocsNote(docs)}"))
            {
                Console.MarkupLine("[grey]Cancelled.[/]");
                return ExitCodes.Success;
            }
        }

        session.Store.Delete(issue);

        if (!numberStillUsed)
        {
            foreach (var blocked in issues.Where(other => other.BlockedBy.Contains(issue.Number)))
                session.Store.Save(blocked with { BlockedBy = blocked.BlockedBy.Where(number => number != issue.Number).ToList() });
        }

        if (docs.Count > 0)
        {
            errors.Console.MarkupLine($"[yellow]Warning:[/] .repo-docs plan steps still link {Markup.Escape(issue.Title)}: {Steps(docs)}");
            errors.Console.MarkupLine("[grey]Update or remove those steps with 'docs edit <address> --step-issues' or '--remove-step'; 'docs check' reports them as MissingIssue.[/]");
        }

        if (settings.Json)
            IssueRenderer.WriteJson(Console, issue, docs);
        else
            IssueRenderer.WriteSaved(Console, "Deleted", issue);

        return ExitCodes.Success;
    }

    static string DocsNote(IReadOnlyList<DocsReference> docs) =>
        docs.Count == 0 ? "" : $" [yellow]Docs plan steps link it:[/] {Steps(docs)}.";

    static string Steps(IReadOnlyList<DocsReference> docs) => Markup.Escape(string.Join(", ", docs.Select(reference => reference.Describe())));
}