using System.ComponentModel;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Plan again, show the plan, ask for confirmation and apply it: plays in file order, hosts of a play in parallel, stopping at the first error without rollback. Exits with 5 when a failure left changes partially applied.")]
public sealed class ApplyCommand(
    IAnsiConsole console,
    ErrorConsole errors,
    ISecretResolver secrets,
    IHostConnector connector,
    ILiveValues live,
    IResourcePlanner resources,
    IResourceApplier resourceApplier,
    IBuildTools builds)
    : HostRunCommand<ApplySettings>(console, errors, new HostRunServices(secrets, connector, live, resources) { Builds = builds })
{
    const int HostConnectAttempts = 18;
    static readonly TimeSpan HostConnectDelay = TimeSpan.FromSeconds(10);

    protected override bool SkipsPrompts(ApplySettings settings) => settings.Yes;

    protected override async Task<int> ExecuteAsync(PlanRun run, ApplySettings settings, Interaction interaction, InfraLog log)
    {
        var plan = run.Plan;
        if (!settings.Json)
            PlanText.Write(Console, plan);

        if (plan.Problems.Count > 0 || !plan.HasActions)
        {
            WriteJson(settings, plan, null);
            return plan.Problems.Count > 0 ? InfraExitCodes.Invalid : InfraExitCodes.Success;
        }

        var destroys = Destroys(plan);
        if (destroys > 0 && !settings.AllowDestroy)
        {
            Errors.MarkupLine($"[red]Error:[/] the plan deletes or replaces {destroys} resource(s); rerun with --allow-destroy to allow it.");
            return InfraExitCodes.Invalid;
        }

        if (!settings.Yes)
        {
            if (!interaction.IsInteractive)
                throw new UsageException("apply needs confirmation; rerun with --yes to apply without a prompt.");
            if (!interaction.Confirm("Apply these changes?") || (destroys > 0 && !ConfirmOwner(run, destroys)))
            {
                Console.MarkupLine("Apply cancelled.");
                return InfraExitCodes.Invalid;
            }
        }

        var resourceResult = await ApplyResourcesAsync(run, settings, log);
        var result = resourceResult.Succeeded
            ? Combine(resourceResult, await new Applier(builds: new ContainerBuilds(builds)).ApplyAsync(run, log, settings.Json ? null : step => WriteStep(step)))
            : resourceResult;
        WriteJson(settings, plan, result);
        if (!settings.Json)
            WriteOutcome(result);

        if (result.Succeeded)
            return InfraExitCodes.Success;
        return result.PartiallyApplied ? InfraExitCodes.PartiallyApplied : InfraExitCodes.Invalid;
    }

    static int Destroys(InfraPlan plan) =>
        plan.Resources.Count(resource => resource.Changes.Any(change => change.Marker is ChangeMarker.Remove or ChangeMarker.Replace));

    bool ConfirmOwner(PlanRun run, int destroys)
    {
        var owner = run.Graph.Marker.Owner;
        var answer = Console.Prompt(new TextPrompt<string>($"Type [bold]{Markup.Escape(owner)}[/] to delete or replace {destroys} resource(s):").AllowEmpty());
        return answer.Trim() == owner;
    }

    async Task<ApplyResult> ApplyResourcesAsync(PlanRun run, ApplySettings settings, InfraLog log)
    {
        var result = await resourceApplier.ApplyAsync(run, log, settings.Json ? null : step => WriteStep(step), CancellationToken.None);
        if (!result.Succeeded || result.Applied == 0)
            return result;

        var planner = new Planner(Services.Connector, Services.Live, Services.Resources);
        var problems = await planner.PlanUnknownHostsAsync(run, HostConnectAttempts, HostConnectDelay);
        var failures = problems.Select(problem => new ApplyStep("", problem.Host ?? "", "", "plan", ApplyOutcome.Failed, problem.Message)).ToList();
        if (!settings.Json)
            failures.ForEach(WriteStep);
        return Combine(result, new ApplyResult(failures));
    }

    static ApplyResult Combine(ApplyResult first, ApplyResult second) => new([.. first.Steps, .. second.Steps]);

    void WriteJson(ApplySettings settings, InfraPlan plan, ApplyResult? result)
    {
        if (settings.Json)
            CliOutput.WriteJson(Console, PlanJson.Serialize(ApplyView.From(plan, result)));
    }

    void WriteStep(ApplyStep step)
    {
        var where = Markup.Escape($"{step.Host} {step.Role} > {step.Label}");
        CliOutput.WriteUnwrappedLine(Console, step.Outcome == ApplyOutcome.Applied
            ? $"  [green]done[/] {where}"
            : $"  [red]failed[/] {where}: {Markup.Escape(step.Error ?? "")}");
    }

    void WriteOutcome(ApplyResult result)
    {
        Console.WriteLine();
        if (result.Failure is { } failure)
            CliOutput.WriteUnwrappedLine(Console, $"[red]Apply failed[/] on {Markup.Escape(failure.Host)}; {result.Applied} task(s) were applied before the failure and are not rolled back.");
        else
            CliOutput.WriteUnwrappedLine(Console, $"Apply complete: {result.Applied} task(s) applied.");
    }
}