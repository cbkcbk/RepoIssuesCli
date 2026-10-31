using System.ComponentModel;
using System.Text.RegularExpressions;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Restore a self-hosted postgres backup (postgres.backup) over its database or into a new one, by running the backup sidecar on its host over SSH. Managed DigitalOcean databases are restored with DigitalOcean's own tools; 'infra db copy' copies data elsewhere.")]
public sealed partial class RestoreCommand(
    IAnsiConsole console,
    ErrorConsole errors,
    ISecretResolver secrets,
    IHostConnector connector,
    ILiveValues live,
    IResourcePlanner resources)
    : InfraCommand<RestoreSettings>(console, errors)
{
    protected override int Run(InfraWorkspace workspace, RestoreSettings settings, InfraLog log) =>
        RunAsync(workspace, settings, log).GetAwaiter().GetResult();

    async Task<int> RunAsync(InfraWorkspace workspace, RestoreSettings settings, InfraLog log)
    {
        var at = CheckUsage(settings);
        RequireInitialized(workspace);
        if (EnvRules.Check(workspace, settings.Env) is { Code: DiagnosticCodes.EnvRequired } missing)
            throw new UsageException(missing.Message + ".");

        var interaction = new Interaction(Console, settings);
        if (!settings.List && !settings.Yes && !interaction.IsInteractive)
            throw new UsageException("restore needs the database name typed as confirmation; rerun with --yes to restore without a prompt.");

        var evaluation = InfraEvaluator.Evaluate(workspace, new EvaluationOptions { Env = settings.Env, Log = log });
        if (evaluation.HasErrors || evaluation.Graph is null)
        {
            ProblemTable.Write(Console, [
                .. evaluation.Diagnostics.Select(diagnostic =>
                    new ProblemRow(diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Location?.ToString()))
            ]);
            return InfraExitCodes.Invalid;
        }

        try
        {
            var target = BackupTarget.Find(evaluation.Graph, settings.Database);
            var lookup = new SecretLookup(secrets, workspace, settings.Env, log);
            var address = await AddressAsync(evaluation.Graph, workspace, lookup, target.Host);
            var passwords = new BecomePasswords(secrets, workspace, settings.Env, Console, interaction.IsInteractive && !settings.Yes, log);
            await using var connection = await connector.ConnectAsync(target.Host, new HostConnectOptions
            {
                Address = address,
                AcceptNewHosts = settings.AcceptNewHosts,
                Log = log,
                BecomePassword = passwords.Provider,
            });

            var sidecar = new BackupSidecar(connection, target);
            var dumps = await sidecar.ListAsync();
            if (settings.List)
            {
                WriteList(target, dumps, settings.Json);
                return InfraExitCodes.Success;
            }

            var dump = BackupDumps.Select(dumps, target.Database, target.Location, settings.Dump, at);
            var view = new RestoreView(target.Database, target.Host.Name, DumpView.From(dump), settings.Into ?? target.Database, settings.Into is null,
                settings.Into is null ? target.Apps : [], false);
            if (!settings.Json)
                WriteSummary(target, address, dump, view);
            if (!settings.Yes && !Confirmed(target.Database))
            {
                Console.MarkupLine("Restore cancelled.");
                return InfraExitCodes.Invalid;
            }

            log.Info("restore.start", $"restoring {dump.Key} into {view.Target} on {target.Host.Name}");
            await sidecar.RestoreAsync(dump, settings.Into);
            if (settings.Json)
                CliOutput.WriteJson(Console, RestoreJson.Serialize(view with { Restored = true }));
            else
                CliOutput.WriteUnwrappedLine(Console, $"[green]Restored[/] {Markup.Escape(dump.Key)} into {Markup.Escape(view.Target)}.");
            return InfraExitCodes.Success;
        }
        catch (Exception exception) when (exception is RestoreRefusedException or HostException)
        {
            CliOutput.WriteUnwrappedLine(Errors, $"[red]Error:[/] {Markup.Escape(exception.Message)}");
            return InfraExitCodes.Invalid;
        }
    }

    static DateTimeOffset? CheckUsage(RestoreSettings settings)
    {
        if (settings.List && (settings.Dump ?? settings.At ?? settings.Into) is not null)
            throw new UsageException("--list can't be combined with --dump, --at or --into.");
        if (settings.Dump is not null && settings.At is not null)
            throw new UsageException("Use --dump or --at, not both.");
        if (settings.Into is { } into)
        {
            if (!DatabaseName().IsMatch(into))
                throw new UsageException($"--into '{into}' must start with a letter or _ and use only letters, digits, _ and -.");
            if (into == settings.Database)
                throw new UsageException($"--into names the live database {into}; leave --into out to restore over it.");
        }

        if (settings.At is not { } text)
            return null;
        return BackupDumps.ParseTime(text) ?? throw new UsageException($"--at '{text}' is not a time; use for example 2026-10-30T03:00Z or 2026-10-30.");
    }

    async Task<string> AddressAsync(InfraGraph graph, InfraWorkspace workspace, SecretLookup lookup, HostNode host)
    {
        if (Address(host) is { } known)
            return known;

        IReadOnlyList<PlanProblem> problems = [];
        if (resources is ILiveResourcePlanner planner)
        {
            await using var run = new PlanRun(graph, workspace, live, lookup);
            problems = (await planner.PlanAsync(run, PlanFilter.All, CancellationToken.None)).Problems;
            if (Address(host) is { } found)
                return found;
        }

        var reasons = problems.Count == 0 ? "" : " (" + string.Join("; ", problems.Select(problem => problem.Message)) + ")";
        throw new HostException(host.Name, $"the address of host '{host.Name}' is not known; it is known after 'infra apply' creates it{reasons}.");
    }

    string? Address(HostNode host) => host.Address switch
    {
        StringValue text => text.Value,
        OutputRef output => live.Resolve(output) is StringValue resolved ? resolved.Value : null,
        _ => null,
    };

    void WriteList(BackupTarget target, IReadOnlyList<BackupDump> dumps, bool json)
    {
        if (json)
        {
            CliOutput.WriteJson(Console, RestoreJson.Serialize(new DumpListView(target.Database, target.Host.Name, target.Location, [.. dumps.Reverse().Select(DumpView.From)])));
            return;
        }
        if (dumps.Count == 0)
        {
            CliOutput.WriteUnwrappedLine(Console, $"No dumps of {Markup.Escape(target.Database)} in {Markup.Escape(target.Location)} yet.");
            return;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumn("Key").AddColumn("Taken").AddColumn(new TableColumn("Size").RightAligned());
        foreach (var dump in dumps.Reverse())
            table.AddRow(Markup.Escape(dump.Key), Markup.Escape(dump.TimeText), Markup.Escape(dump.SizeText));
        Console.Write(table);
    }

    void WriteSummary(BackupTarget target, string address, BackupDump dump, RestoreView view)
    {
        CliOutput.WriteUnwrappedLine(Console, $"[bold]Restore postgres database {Markup.Escape(target.Database)}[/]");
        Row("dump", dump.Key);
        Row("taken", dump.TimeText);
        Row("size", dump.SizeText);
        Row("host", $"{target.Host.User}@{address} {target.Where}");
        Row("into", view.OverLive
            ? $"the live database {target.Database}: its objects are dropped and recreated from the dump in one transaction"
            : $"the new database {view.Target} on the same server; {target.Database} is not touched");
        if (view.Apps.Count > 0)
            CliOutput.WriteUnwrappedLine(Console,
                $"[yellow]Warning:[/] app(s) {Markup.Escape(string.Join(", ", view.Apps))} use {Markup.Escape(target.Database)}; they keep running and may fail while the restore holds its locks, and everything written since the dump was taken is replaced.");
    }

    void Row(string label, string value) => CliOutput.WriteUnwrappedLine(Console, $"  [grey]{label,-6}[/] {Markup.Escape(value)}");

    bool Confirmed(string database)
    {
        var answer = Console.Prompt(new TextPrompt<string>($"Type [bold]{Markup.Escape(database)}[/] to restore:").AllowEmpty());
        return answer.Trim() == database;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex DatabaseName();
}
