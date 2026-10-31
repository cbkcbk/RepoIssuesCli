using System.Collections.Concurrent;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Postgres;
using RepoInfraCli.Features.Infra.Secrets;

namespace RepoInfraCli.Features.Infra.Plan;

public enum ApplyOutcome
{
    Applied,
    Failed,
}

public sealed record ApplyStep(string Play, string Host, string Role, string Label, ApplyOutcome Outcome, string? Error = null);

public sealed record ApplyResult(IReadOnlyList<ApplyStep> Steps)
{
    public ApplyStep? Failure => Steps.FirstOrDefault(step => step.Outcome == ApplyOutcome.Failed);

    public int Applied => Steps.Count(step => step.Outcome == ApplyOutcome.Applied);

    public bool Succeeded => Failure is null;

    public bool PartiallyApplied => Failure is not null && Applied > 0;
}

public sealed class Applier(HostTaskRegistry? tasks = null, ContainerBuilds? builds = null, CopySteps? copies = null)
{
    public const string BuildsPlay = "builds";
    public const string CopiesPlay = "copies";
    public const string LocalHost = "local";

    readonly HostTaskRegistry modules = tasks ?? HostTaskRegistry.Default;
    readonly ContainerBuilds containerBuilds = builds ?? ContainerBuilds.Default;
    readonly CopySteps copySteps = copies ?? CopySteps.Default;

    public async Task<ApplyResult> ApplyAsync(PlanRun run, InfraLog log, Action<ApplyStep>? progress = null, CancellationToken cancellationToken = default)
    {
        var steps = new ConcurrentQueue<ApplyStep>();

        void Record(ApplyStep step)
        {
            steps.Enqueue(step);
            log.Write(step.Outcome == ApplyOutcome.Failed ? InfraLogLevel.Error : InfraLogLevel.Debug, "apply.step",
                $"{step.Host} {step.Label}: {step.Error ?? "applied"}",
                new Dictionary<string, object?> { ["play"] = step.Play, ["host"] = step.Host, ["role"] = step.Role, ["task"] = step.Label, ["outcome"] = step.Outcome.ToString() });
            progress?.Invoke(step);
        }

        if (!await ApplyBuildsAsync(run, Record, cancellationToken) || !await ApplyCopiesAsync(run, log, Record, cancellationToken))
            return new ApplyResult([.. steps]);

        foreach (var play in run.Plan.Plays)
        {
            using var stop = new CancellationTokenSource();
            var hosts = play.Hosts.Where(host => host.Status == HostStatus.Planned && host.Tasks.Any(task => task.Probe.NeedsApply));
            await Task.WhenAll(hosts.Select(host => ApplyHostAsync(run, play, host, Record, stop, cancellationToken)));
            if (steps.Any(step => step.Outcome == ApplyOutcome.Failed))
                break;
        }

        return new ApplyResult([.. steps]);
    }

    async Task<bool> ApplyBuildsAsync(PlanRun run, Action<ApplyStep> record, CancellationToken cancellationToken)
    {
        foreach (var build in run.Plan.Builds.Where(build => build.NeedsBuild))
        {
            var role = string.Join(", ", build.Apps);
            try
            {
                await containerBuilds.ApplyAsync(build, run.Workspace, cancellationToken);
                record(new ApplyStep(BuildsPlay, LocalHost, role, build.Label, ApplyOutcome.Applied));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                record(new ApplyStep(BuildsPlay, LocalHost, role, build.Label, ApplyOutcome.Failed, exception.Message));
                return false;
            }
        }
        return true;
    }

    async Task<bool> ApplyCopiesAsync(PlanRun run, InfraLog log, Action<ApplyStep> record, CancellationToken cancellationToken)
    {
        foreach (var copy in run.Plan.Copies)
        {
            try
            {
                await copySteps.ApplyAsync(copy, run, log, cancellationToken);
                record(new ApplyStep(CopiesPlay, LocalHost, CopyNode.Kind, copy.Label, ApplyOutcome.Applied));
            }
            catch (Exception exception) when (exception is DatabaseCopyException or HostException or SecretUnavailableException)
            {
                record(new ApplyStep(CopiesPlay, LocalHost, CopyNode.Kind, copy.Label, ApplyOutcome.Failed, exception.Message));
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                record(new ApplyStep(CopiesPlay, LocalHost, CopyNode.Kind, copy.Label, ApplyOutcome.Failed, CtrlC.Message));
                return false;
            }
        }
        return true;
    }

    async Task ApplyHostAsync(PlanRun run, PlayPlan play, HostPlan host, Action<ApplyStep> record, CancellationTokenSource stop, CancellationToken cancellationToken)
    {
        var connection = run.Connections[host.Host.Id];
        var changed = new HashSet<NodeId>();
        var handlers = new List<(string Role, TaskPlan Task)>();

        async Task<bool> ApplyAsync(string role, TaskPlan task)
        {
            if (stop.IsCancellationRequested)
                return false;
            try
            {
                await modules.For(task.Task.Kind).ApplyAsync(new TaskContext(connection, task.Task, run.Workspace, run.Live, run.Secrets), task.Probe, cancellationToken);
                record(new ApplyStep(play.Name, host.Host.Name, role, task.Label, ApplyOutcome.Applied));
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                record(new ApplyStep(play.Name, host.Host.Name, role, task.Label, ApplyOutcome.Failed, exception.Message));
                await stop.CancelAsync();
                return false;
            }
        }

        foreach (var role in host.Roles.Where(role => !role.External))
        foreach (var task in role.Tasks)
        {
            if (task.Task.Kind == TaskKinds.Handler)
            {
                handlers.Add((role.Name, task));
                continue;
            }

            if (!task.Probe.NeedsApply)
                continue;
            if (!await ApplyAsync(role.Name, task))
                return;
            if (task.Probe.HasDrift)
                changed.Add(task.Task.Id);
        }

        var notified = host.Tasks.Where(task => changed.Contains(task.Task.Id) && task.Task.Notifies is not null).Select(task => task.Task.Notifies!.Value).ToHashSet();
        foreach (var (role, handler) in handlers.Where(handler => notified.Contains(handler.Task.Task.Id)))
        {
            if (!await ApplyAsync(role, handler))
                return;
        }
    }
}