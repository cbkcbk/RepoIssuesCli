using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Evaluation;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands;

public sealed record CloudTarget(string Provider, string Kind, string Name, ResourceHandler Handler, ProviderSession Session, ResourceNode? Node, LiveResource? Live)
{
    public string Label => $"{Provider} {Kind} {Name}";
}

public abstract class CloudCommand<TSettings>(IAnsiConsole console, ErrorConsole errors, ISecretResolver secrets, CloudServices cloud)
    : InfraCommand<TSettings>(console, errors)
    where TSettings : InfraSettings
{
    protected CloudServices Cloud => cloud;

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
            ProblemTable.Write(Console, [
                .. evaluation.Diagnostics.Select(diagnostic =>
                    new ProblemRow(diagnostic.Code, diagnostic.Severity, diagnostic.Message, diagnostic.Location?.ToString()))
            ]);
            return InfraExitCodes.Invalid;
        }

        await using var run = new PlanRun(evaluation.Graph, workspace, cloud.Live, new SecretLookup(secrets, workspace, settings.Env, log));
        var inventory = await cloud.ScanAsync(run, log, ScansAllKinds, CancellationToken.None);
        return await ExecuteAsync(run, inventory, settings, new Interaction(Console, settings), log);
    }

    protected virtual bool ScansAllKinds => false;

    protected abstract Task<int> ExecuteAsync(PlanRun run, CloudInventory inventory, TSettings settings, Interaction interaction, InfraLog log);

    protected bool WriteProblems(CloudInventory inventory)
    {
        foreach (var problem in inventory.Problems)
            CliOutput.WriteUnwrappedLine(Errors, $"[red]error[/] {Markup.Escape(problem.Message)}");
        return inventory.Problems.Count > 0;
    }

    protected static IReadOnlyList<CloudTarget> Targets(CloudInventory inventory) =>
    [
        .. inventory.Order.Where(node => inventory.Matches.ContainsKey(node.Id)).Select(node =>
        {
            var match = inventory.Matches[node.Id];
            return new CloudTarget(node.Provider, node.Kind, match.Handler.Display(node), match.Handler, match.Session, node, match.Live ?? match.Unowned);
        }),
        .. inventory.Strays.Select(stray =>
            new CloudTarget(stray.Session.Provider.Kind, stray.Handler.Kind, stray.Handler.Display(stray.Live), stray.Handler, stray.Session, null, stray.Live)),
    ];

    protected static IReadOnlyList<CloudTarget> Select(IReadOnlyList<CloudTarget> targets, IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
            return targets;

        var selected = new List<CloudTarget>();
        foreach (var name in names)
        {
            var matches = targets.Where(target => Matches(target, name)).ToList();
            if (matches.Count == 0)
                throw new CloudTargetNotFoundException($"No resource named '{name}'. Resources: {string.Join(", ", targets.Select(target => $"{target.Kind}:{target.Name}"))}.");
            if (matches.Count > 1)
                throw new UsageException($"'{name}' matches {string.Join(", ", matches.Select(target => $"{target.Kind}:{target.Name}"))}; use kind:name.");
            selected.Add(matches[0]);
        }
        return [.. selected.Distinct()];
    }

    static bool Matches(CloudTarget target, string name) =>
        name == target.Name || name == $"{target.Kind}:{target.Name}" || (target.Node is { } node && (name == node.Name || name == $"{target.Kind}:{node.Name}"));
}

public sealed class CloudTargetNotFoundException(string message) : NotFoundException(message);
