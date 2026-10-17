using RepoIssuesCli.Features.Issues;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli;

public sealed record IssueSession(IssueStore Store, IssueSnapshot Snapshot, Interaction Interaction)
{
    public IReadOnlyList<Issue> Issues => Snapshot.Issues;
}

public abstract class IssueCommand<TSettings>(IAnsiConsole console) : Command<TSettings>
    where TSettings : GlobalSettings
{
    protected IAnsiConsole Console => console;

    public override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        var store = IssueStore.Open(settings.Root);
        return Run(new IssueSession(store, store.Load(), new Interaction(console, settings)), settings);
    }

    protected abstract int Run(IssueSession session, TSettings settings);
}
