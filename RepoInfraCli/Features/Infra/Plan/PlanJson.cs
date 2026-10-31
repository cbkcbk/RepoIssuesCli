using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Hosts.Tasks;
using RepoInfraCli.Features.Infra.Postgres;

namespace RepoInfraCli.Features.Infra.Plan;

public sealed record PlanView(
    bool Changes,
    PlanSummary Summary,
    IReadOnlyList<ResourceView>? Resources,
    IReadOnlyList<PlayView> Plays,
    IReadOnlyList<ProblemView> Problems)
{
    public IReadOnlyList<BuildView>? Builds { get; init; }

    public IReadOnlyList<CopyView>? Copies { get; init; }

    public static PlanView From(InfraPlan plan) => new(
        plan.HasDrift,
        plan.Summary,
        plan.ShowsResources ? [.. plan.Resources.Select(ResourceView.From)] : null,
        [.. plan.Plays.Select(PlayView.From)],
        [.. plan.Problems.Select(problem => new ProblemView(problem.Host, problem.Message))])
    {
        Builds = plan.Builds.Count > 0 ? [.. plan.Builds.Select(BuildView.From)] : null,
        Copies = plan.Copies.Count > 0 ? [.. plan.Copies.Select(CopyView.From)] : null,
    };
}

public sealed record CopyView(string Source, string Target, string? Scrub, string? Migrate, bool Protected, string? File, int? Line, IReadOnlyList<ChangeView> Changes)
{
    public static CopyView From(CopyPlan copy) => new(
        copy.From,
        copy.To,
        copy.Copy.Scrub,
        copy.Migrate,
        copy.Protected,
        copy.Copy.Location?.File,
        copy.Copy.Location?.Line,
        [.. copy.Changes.Select(ChangeView.From)]);
}

public sealed record BuildView(string Kind, string Name, IReadOnlyList<string> Apps, string? Image, IReadOnlyList<ChangeView> Changes, string? Note, string? Error)
{
    public static BuildView From(BuildPlan build) => new(
        build.Build.Kind,
        build.Label,
        build.Apps,
        build.Image,
        [.. build.Changes.Select(ChangeView.From)],
        build.Note,
        build.Error);
}

public sealed record ResourceView(string Provider, string Kind, string Name, IReadOnlyList<ChangeView> Changes)
{
    public static ResourceView From(ResourceChange resource) =>
        new(resource.Provider, resource.Kind, resource.Name, [.. resource.Changes.Select(ChangeView.From)]);
}

public sealed record PlayView(string Name, IReadOnlyList<HostView> Hosts)
{
    public static PlayView From(PlayPlan play) => new(play.Name, [.. play.Hosts.Select(HostView.From)]);
}

public sealed record HostView(string Name, string? Address, string Status, string? Note, IReadOnlyList<RoleView> Roles)
{
    public static HostView From(HostPlan host) => new(
        host.Host.Name,
        host.Address,
        JsonNamingPolicy.CamelCase.ConvertName(host.Status.ToString()),
        host.Note,
        [.. host.Roles.Select(RoleView.From)]);
}

public sealed record RoleView(string Name, bool External, IReadOnlyList<TaskView> Tasks)
{
    public static RoleView From(RolePlan role) =>
        new(role.Name, role.External, [.. role.Tasks.Where(task => task.Changes.Count > 0 || task.Error is not null).Select(TaskView.From)]);
}

public sealed record TaskView(string Kind, string? Name, string Label, string? File, int? Line, IReadOnlyList<ChangeView> Changes, string? Error)
{
    public static TaskView From(TaskPlan task) => new(
        task.Task.Kind,
        task.Task.Name,
        task.Label,
        task.Task.Location?.File,
        task.Task.Location?.Line,
        [.. task.Changes.Select(ChangeView.From)],
        task.Error);
}

public sealed record ChangeView(string Marker, string Action, string Text)
{
    public static ChangeView From(Change change) =>
        new(change.Marker.Symbol(), JsonNamingPolicy.CamelCase.ConvertName(change.Marker.ToString()), change.Text);
}

public sealed record ProblemView(string? Host, string Message);

public sealed record ApplyView(bool Applied, PlanView Plan, IReadOnlyList<StepView> Steps, StepView? Failure)
{
    public static ApplyView From(InfraPlan plan, ApplyResult? result) => new(
        result is { Succeeded: true },
        PlanView.From(plan),
        result is null ? [] : [.. result.Steps.Select(StepView.From)],
        result?.Failure is { } failure ? StepView.From(failure) : null);
}

public sealed record StepView(string Play, string Host, string Role, string Task, string Outcome, string? Error)
{
    public static StepView From(ApplyStep step) =>
        new(step.Play, step.Host, step.Role, step.Label, JsonNamingPolicy.CamelCase.ConvertName(step.Outcome.ToString()), step.Error);
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PlanView))]
[JsonSerializable(typeof(ApplyView))]
public partial class PlanJsonContext : JsonSerializerContext;

public static class PlanJson
{
    public static string Serialize<T>(T value) => RepoJson.Serialize(value, PlanJsonContext.Default.Options);
}
