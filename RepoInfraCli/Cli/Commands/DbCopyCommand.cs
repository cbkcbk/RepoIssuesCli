using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands;

public sealed class DbCopySettings : InfraSettings
{
    [CommandArgument(0, "<DATABASE>")]
    [Description("The declared database to copy (postgres.database or a cluster database, as name or cluster/name).")]
    public string Database { get; init; } = "";

    [CommandOption("--to <TARGET>")]
    [Description("local:<db> (INFRA_LOCAL_PG, default postgres://postgres@localhost:5432) or a declared database.")]
    public string? To { get; init; }

    [CommandOption("--scrub <FILE>")]
    [Description("SQL run in one transaction on the copy before it replaces the target. Required when copying a cloud database to local:.")]
    public string? Scrub { get; init; }

    [CommandOption("--migrate")]
    [Description("Run dotnet ef database update on the copy, using the app named by a matching postgres.copy step or the one app whose deploy uses the database.")]
    public bool Migrate { get; init; }

    [CommandOption("--confirm <OWNER>")]
    [Description("The owner name (<project> or <project>-<env>) to confirm replacing a protected target without a prompt.")]
    public string? Confirm { get; init; }
}

[Description("Copy a declared postgres database to local:<db> or another declared database: pg_dump | pg_restore into <db>__infra_tmp_<timestamp>, optional scrub and migrations, then drop the old target and rename the copy. Until that swap the target is untouched; failures and Ctrl+C remove the copy.")]
public sealed class DbCopyCommand(IAnsiConsole console, ErrorConsole errors, ISecretResolver secrets, CloudServices cloud, IHostConnector connector)
    : CloudCommand<DbCopySettings>(console, errors, secrets, cloud)
{
    protected override async Task<int> ExecuteAsync(PlanRun run, CloudInventory inventory, DbCopySettings settings, Interaction interaction, InfraLog log)
    {
        var graph = run.Graph;
        var to = settings.To ?? throw new UsageException("db copy needs --to local:<db> or a declared database.");
        var source = DeclaredDatabases.Find(graph, settings.Database);
        var local = to.StartsWith(DatabaseEndpoints.LocalPrefix, StringComparison.Ordinal) ? to[DatabaseEndpoints.LocalPrefix.Length..] : null;
        if (local is "")
            throw new UsageException("--to local: needs a database name, as in local:api_dev.");
        var target = local is null ? DeclaredDatabases.Find(graph, to) : null;
        var targetName = target?.Display ?? to;

        if (target?.Owner is { } owner)
            return Fail(settings, source.Display, targetName, $"\"{target.Display}\" comes from repo.provider ({owner.Label}); copy into it from the repo that declares it");
        if (target?.Id == source.Id)
            return Fail(settings, source.Display, targetName, $"source and target are both \"{source.Display}\"; refusing to copy a database onto itself");
        if (CopyRules.ScrubRequired(source.Managed, local is not null, settings.Scrub is not null) is { } unscrubbed)
            return Fail(settings, source.Display, targetName, unscrubbed);
        var scrub = settings.Scrub is { } file ? Path.GetFullPath(file) : null;
        if (scrub is not null && !File.Exists(scrub))
            throw new UsageException($"--scrub file {settings.Scrub} does not exist.");
        if ((source.Managed || target is { Managed: true }) && WriteProblems(inventory))
            return InfraExitCodes.Invalid;
        if (!Confirmed(run, target, settings, interaction))
            return Fail(settings, source.Display, targetName, "copy cancelled");

        var steps = new List<string>();
        using var interrupt = new CtrlC(CancellationToken.None);
        using var endpoints = new DatabaseEndpoints(graph, run.Live, run.Secrets, (host, token) => ConnectAsync(run, settings, interaction, log, host, token), log);
        try
        {
            var project = settings.Migrate ? CopySteps.MigrationsProject(graph, run.Workspace, CopySteps.MigrationsApp(graph, source.Id, target?.Id).Id) : null;
            var request = new CopyRequest(
                await endpoints.ResolveAsync(source.Id, interrupt.Token),
                target is null ? DatabaseEndpoints.Local(local!) : await endpoints.ResolveAsync(target.Id, interrupt.Token))
            {
                DeclaredMajor = source.Major,
                Scrub = scrub,
                MigrationsProject = project,
            };
            if (!settings.Json)
                CliOutput.WriteUnwrappedLine(Console, $"Copying [bold]{Markup.Escape(source.Display)}[/] to [bold]{Markup.Escape(targetName)}[/]");
            await DatabaseCopier.Default.CopyAsync(request, log, step =>
            {
                steps.Add(step);
                if (!settings.Json)
                    CliOutput.WriteUnwrappedLine(Console, $"  [grey]{Markup.Escape(step)}[/]");
            }, interrupt.Token);
        }
        catch (Exception exception) when (exception is DatabaseCopyException or HostException or SecretUnavailableException)
        {
            return Fail(settings, source.Display, targetName, exception.Message, steps);
        }
        catch (OperationCanceledException) when (interrupt.Token.IsCancellationRequested)
        {
            return Fail(settings, source.Display, targetName, CtrlC.Message, steps);
        }

        if (settings.Json)
            CliOutput.WriteJson(Console, DbCopyJson.Serialize(new DbCopyView(true, source.Display, targetName, steps, null)));
        else
            CliOutput.WriteUnwrappedLine(Console, $"Copied {Markup.Escape(source.Display)} to {Markup.Escape(targetName)}.");
        return InfraExitCodes.Success;
    }

    bool Confirmed(PlanRun run, DeclaredDatabase? target, DbCopySettings settings, Interaction interaction)
    {
        var owner = run.Graph.Marker.Owner;
        var isProtected = target is not null && CopyRules.IsProtected(run.Graph, target.Id);
        return CopyRules.Confirm(isProtected, owner, settings.Confirm, interaction.IsInteractive) switch
        {
            Confirmation.NotNeeded or Confirmation.Given => true,
            Confirmation.Wrong => throw new UsageException($"--confirm {settings.Confirm} does not match the owner {owner}."),
            Confirmation.Missing => throw new UsageException($"{target!.Display} is protected; rerun with --confirm {owner} to replace it."),
            _ => Console.Prompt(new TextPrompt<string>($"[bold]{Markup.Escape(target!.Display)}[/] is protected. Type [bold]{Markup.Escape(owner)}[/] to replace it:").AllowEmpty())
                .Trim() == owner,
        };
    }

    async Task<IHostConnection> ConnectAsync(PlanRun run, DbCopySettings settings, Interaction interaction, InfraLog log, NodeId id, CancellationToken cancellationToken)
    {
        if (run.Connections.TryGetValue(id, out var existing))
            return existing;
        var host = run.Graph.Host(id) ?? throw new DatabaseCopyException($"{id} is not a declared host");
        var address = host.Address switch
        {
            StringValue text => text.Value,
            OutputRef output => (run.Live.Resolve(output) as StringValue)?.Value,
            _ => null,
        } ?? throw new DatabaseCopyException($"host {host.Name} has no address yet; apply creates it");

        var passwords = new BecomePasswords(Secrets, run.Workspace, settings.Env, Console, interaction.IsInteractive, log);
        var connection = await connector.ConnectAsync(host, new HostConnectOptions { Address = address, Log = log, BecomePassword = passwords.Provider }, cancellationToken);
        run.Connections[id] = connection;
        return connection;
    }

    int Fail(DbCopySettings settings, string from, string to, string error, IReadOnlyList<string>? steps = null)
    {
        if (settings.Json)
            CliOutput.WriteJson(Console, DbCopyJson.Serialize(new DbCopyView(false, from, to, steps ?? [], error)));
        else
            CliOutput.WriteUnwrappedLine(Errors, $"[red]Error:[/] {Markup.Escape(error)}");
        return InfraExitCodes.Invalid;
    }
}

public sealed record DbCopyView(bool Copied, string From, string To, IReadOnlyList<string> Steps, string? Error);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DbCopyView))]
public partial class DbCopyJsonContext : JsonSerializerContext;

public static class DbCopyJson
{
    public static string Serialize<T>(T value) => RepoJson.Serialize(value, DbCopyJsonContext.Default.Options);
}
