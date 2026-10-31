using System.ComponentModel;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

[Description("Connect to the hosts of the selected plays, read their facts and show what apply would change: + add, ~ change, - remove, ! always runs, ? handled later. Changes nothing.")]
public sealed class PlanCommand(IAnsiConsole console, ErrorConsole errors, ISecretResolver secrets, IHostConnector connector, ILiveValues live, IResourcePlanner resources, IBuildTools builds)
    : HostRunCommand<PlanSettings>(console, errors, new HostRunServices(secrets, connector, live, resources) { Builds = builds })
{
    protected override Task<int> ExecuteAsync(PlanRun run, PlanSettings settings, Interaction interaction, InfraLog log)
    {
        var plan = run.Plan;
        if (settings.Json)
            CliOutput.WriteJson(Console, PlanJson.Serialize(PlanView.From(plan)));
        else
            PlanText.Write(Console, plan);

        if (plan.Problems.Count > 0)
            return Task.FromResult(InfraExitCodes.Invalid);
        return Task.FromResult(settings.ExitCode && plan.HasDrift ? InfraExitCodes.Drift : InfraExitCodes.Success);
    }
}
