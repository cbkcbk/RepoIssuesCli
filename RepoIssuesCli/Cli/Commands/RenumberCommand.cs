using System.ComponentModel;
using RepoCli.Core.Features.IssueLinks;
using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli.Commands;

[Description("Give an issue the next free number, e.g. to fix a duplicate number after a merge. Blockers and .repo-docs plan steps on an unshared number follow the issue; on a duplicated number they keep pointing at the issue that keeps it.")]
public sealed class RenumberCommand(IAnsiConsole console, ErrorConsole errors) : IssueCommand<RenumberCommand.Settings>(console)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[ref]")]
        [Description("Issue number, name (or unique prefix), title or id. Use the id or name to pick one of several issues sharing a number.")]
        public string? Reference { get; init; }
    }

    protected override int Run(IssueSession session, Settings settings)
    {
        var chosen = session.Interaction.ChooseIssue(session.Issues, settings.Reference, "Renumber which issue?");

        using var lease = session.Store.Lock();
        var snapshot = session.Store.Load();
        var issue = snapshot.Issues.FirstOrDefault(candidate => candidate.Id == chosen.Id)
                    ?? throw new IssueNotFoundException(chosen.Id.ToString());
        var result = IssueRenumbering.Renumber(snapshot, issue);

        var docs = new DocsSteps(session.Store.Root) { LockTimeout = session.Store.LockTimeout };
        using var docsLease = docs.Lock();

        session.Store.Save(result.Issue);
        foreach (var rewritten in result.Rewritten)
            session.Store.Save(rewritten);

        (result, var scan) = IssueRenumbering.UpdateDocs(result, docs);
        errors.Report(scan);

        if (settings.Json)
            IssueRenderer.WriteJson(Console, result);
        else
            WriteSummary(snapshot.Issues, issue, result);

        return ExitCodes.Success;
    }

    void WriteSummary(IReadOnlyList<Issue> issues, Issue original, RenumberResult result)
    {
        Console.MarkupLine($"Renumbered [bold]{Markup.Escape(original.Title)}[/] to [bold]{Markup.Escape(result.Issue.Title)}[/]");

        if (result.Rewritten.Count > 0)
            Console.MarkupLine($"[grey]Updated blockers of:[/] {Titles(result.Rewritten)}");
        if (result.DocsReferences.Count > 0)
            Console.MarkupLine($"[grey]Updated docs steps:[/] {Steps(result.DocsReferences)}");

        var holders = issues.Where(other => other.Id != original.Id && other.Number == result.PreviousNumber).ToList();
        if (result.KeptReferences.Count > 0)
        {
            Console.MarkupLine($"[yellow]Blockers on {result.PreviousNumber:D4} still mean {Titles(holders)}:[/] {Titles(result.KeptReferences)}");
            Console.MarkupLine($"[grey]If one meant {Markup.Escape(result.Issue.Title)}, run 'issues edit <ref> --remove-blocker {result.PreviousNumber} --add-blocker {result.Issue.Number}'.[/]");
        }

        if (result.KeptDocsReferences.Count > 0)
        {
            Console.MarkupLine($"[yellow]Docs steps on {result.PreviousNumber:D4} still mean {Titles(holders)}:[/] {Steps(result.KeptDocsReferences)}");
            Console.MarkupLine($"[grey]If one meant {Markup.Escape(result.Issue.Title)}, run 'docs edit <address> --step-issues <step>=...' with {result.Issue.Number} in place of {result.PreviousNumber}.[/]");
        }
    }

    static string Titles(IEnumerable<Issue> issues) => Markup.Escape(string.Join(", ", issues.Select(issue => issue.Title)));

    static string Steps(IEnumerable<DocsReference> references) => Markup.Escape(string.Join(", ", references.Select(reference => reference.Describe())));
}