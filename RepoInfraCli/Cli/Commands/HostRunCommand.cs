using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

public sealed record HostRunServices(ISecretResolver Secrets, IHostConnector Connector, ILiveValues Live, IResourcePlanner Resources)
{
    public IBuildTools Builds { get; init; } = DotnetBuildTools.Default;
}

public abstract class HostRunCommand<TSettings>(IAnsiConsole console, ErrorConsole errors, HostRunServices services) : InfraCommand<TSettings>(console, errors)
    where TSettings : HostRunSettings
{
    protected HostRunServices Services => services;

    protected override int Run(InfraWorkspace workspace, TSettings settings, InfraLog log) =>
        RunAsync(workspace, settings, log).GetAwaiter().GetResult();

    async Task<int> RunAsync(InfraWorkspace workspace, TSettings settings, InfraLog log)
    {
        RequireInitialized(workspace);
        if (EnvRules.Check(workspace, settings.Env) is { Code: DiagnosticCodes.EnvRequired } missing)
            throw new UsageException(missing.Message + ".");

        var evaluation = InfraEvaluator.Evaluate(workspace, new EvaluationOptions { Env = settings.Env, Log = log });
        if (evaluation.HasErrors || evaluation.Graph is null)
        {
            WriteEvaluationErrors(evaluation, settings.Json);
            return InfraExitCodes.Invalid;
        }

        var interaction = new Interaction(Console, settings);
        var passwords = new BecomePasswords(services.Secrets, workspace, settings.Env, Console, interaction.IsInteractive && !SkipsPrompts(settings), log);
        var planner = new Planner(services.Connector, services.Live, services.Resources, builds: new ContainerBuilds(services.Builds));
        await using var run = await planner.PlanAsync(evaluation.Graph, new PlanOptions
        {
            Workspace = workspace,
            Filter = settings.Filter,
            AcceptNewHosts = settings.AcceptNewHosts,
            Log = log,
            BecomePassword = passwords.Provider,
            Secrets = new SecretLookup(services.Secrets, workspace, settings.Env, log),
            AllowDirty = settings.AllowDirty,
        });
        return await ExecuteAsync(run, settings, interaction, log);
    }

    protected virtual bool SkipsPrompts(TSettings settings) => false;

    protected abstract Task<int> ExecuteAsync(PlanRun run, TSettings settings, Interaction interaction, InfraLog log);

    void WriteEvaluationErrors(EvaluationResult evaluation, bool json)
    {
        if (json)
        {
            CliOutput.WriteJson(Console, InfraJson.SerializeOutput(new ValidateResult(
                false,
                [.. evaluation.Errors.Select(DiagnosticView.From)],
                [.. evaluation.Warnings.Select(DiagnosticView.From)],
                null)));
            return;
        }

        ProblemTable.Write(Console, [
            .. evaluation.Diagnostics.Select(diagnostic =>
                new ProblemRow(diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Location?.ToString()))
        ]);
    }
}