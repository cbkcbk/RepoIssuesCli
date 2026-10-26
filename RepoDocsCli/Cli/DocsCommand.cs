using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli;

public sealed record ErrorConsole(IAnsiConsole Console);

public abstract class DocsCommand<TSettings>(IAnsiConsole console, ErrorConsole errors) : Command<TSettings>
    where TSettings : GlobalSettings
{
    protected IAnsiConsole Console => console;

    protected IAnsiConsole Errors => errors.Console;

    public override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return Run(DocsStore.Open(settings.Root), settings);
        }
        catch (DocsFileException exception)
        {
            Errors.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
            return ExitCodes.ValidationFailed;
        }
    }

    protected abstract int Run(DocsStore store, TSettings settings);

    protected DocsSnapshot LoadReportingErrors(DocsStore store)
    {
        var snapshot = store.Load();
        foreach (var error in snapshot.Errors)
            Errors.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(error.FileName)}: {Markup.Escape(error.Message)} [grey](file skipped)[/]");
        return snapshot;
    }

    protected static void RequireInitialized(DocsStore store)
    {
        if (!store.IsInitialized)
            throw new UsageException($"No {RepoLocator.DocsFolderName} folder in {store.Root}; run 'docs init' first.");
    }

    protected void ReportPlanIssues(PlanLookup plans)
    {
        if (plans.Loaded is not { } issues)
            return;
        if (!issues.IsInitialized)
            Errors.MarkupLine($"[yellow]Warning:[/] No {RepoLocator.IssuesFolderName} folder; plan progress is unavailable.");
        foreach (var error in issues.Errors)
            Errors.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(error.FileName)}: {Markup.Escape(error.Message)} [grey](issue skipped)[/]");
    }
}
