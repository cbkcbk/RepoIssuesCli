using RepoInfraCli.Features.Infra.Hosts.Tasks;
using Spectre.Console;

namespace RepoInfraCli.Features.Infra.Plan;

public static class PlanText
{
    public static void Write(IAnsiConsole console, InfraPlan plan)
    {
        if (plan.ShowsResources)
        {
            Line(console, "[bold]Resources[/]");
            if (plan.Resources.Count == 0)
                Line(console, "  [grey]no changes[/]");
            foreach (var resource in plan.Resources)
            foreach (var change in resource.Changes)
                Line(console, $"  {Marker(change)} {Markup.Escape($"{resource.Provider} {resource.Kind} {resource.Name}")}: {Markup.Escape(change.Text)}");
            foreach (var problem in plan.Problems.Where(problem => problem.Host is null))
                Line(console, $"  [red]error[/] {Markup.Escape(problem.Message)}");
        }

        WriteBuilds(console, plan);

        Line(console, "[bold]Plays[/]");
        if (plan.Plays.Count == 0)
            Line(console, "  [grey]no plays selected[/]");
        foreach (var play in plan.Plays)
        {
            Line(console, $"  [bold]{Markup.Escape(play.Name)}[/]");
            foreach (var host in play.Hosts)
                WriteHost(console, host);
        }

        console.WriteLine();
        Line(console, Summary(plan));
    }

    static void WriteBuilds(IAnsiConsole console, InfraPlan plan)
    {
        if (plan.Builds.Count == 0)
            return;
        Line(console, "[bold]Builds[/]");
        foreach (var build in plan.Builds)
        {
            var label = Markup.Escape(build.Label);
            if (build.Error is not null)
                Line(console, $"  [red]error[/] {label}: {Markup.Escape(build.Error)}");
            foreach (var change in build.Changes)
                Line(console, $"  {Marker(change)} {label}: {Markup.Escape(change.Text)}");
            if (build.Error is null && build.Changes.Count == 0)
                Line(console, $"  {label} [grey]({Markup.Escape(build.Note ?? "no changes")})[/]");
        }
    }

    static void WriteHost(IAnsiConsole console, HostPlan host)
    {
        var name = Markup.Escape(host.Host.Name);
        switch (host.Status)
        {
            case HostStatus.Failed:
                Line(console, $"    {name} [red]error:[/] {Markup.Escape(host.Note ?? "")}");
                return;
            case HostStatus.AddressUnknown or HostStatus.External:
                Line(console, $"    {name} [grey]({Markup.Escape(host.Note ?? "")})[/]");
                return;
        }

        Line(console, $"    {name} [grey]{Markup.Escape($"{host.Host.User}@{host.Address}")}[/]");
        var shown = false;
        foreach (var role in host.Roles)
        {
            if (role.External)
            {
                Line(console, $"      {Markup.Escape(role.Name)} [grey](external role; handled in a later milestone (#49))[/]");
                shown = true;
                continue;
            }

            var tasks = role.Tasks.Where(task => task.Changes.Count > 0 || task.Error is not null).ToList();
            if (tasks.Count == 0)
                continue;
            shown = true;
            Line(console, $"      {Markup.Escape(role.Name)}");
            foreach (var task in tasks)
            {
                if (task.Error is not null)
                    Line(console, $"        [red]error[/] {Markup.Escape(task.Label)}: {Markup.Escape(task.Error)}");
                foreach (var change in task.Changes)
                    Line(console, $"        {Marker(change)} {Markup.Escape(task.Label)}: {Markup.Escape(change.Text)}");
            }
        }

        if (!shown)
            Line(console, "      [grey]no changes[/]");
    }

    public static string Summary(InfraPlan plan)
    {
        var summary = plan.Summary;
        if (!plan.HasActions && summary.Unsupported == 0)
            return plan.Problems.Count > 0 ? $"[red]{plan.Problems.Count} problem(s)[/] prevented a complete plan." : "No changes.";

        var parts = new List<string>
        {
            $"{summary.Add} to add",
            $"{summary.Change} to change",
            $"{summary.Remove} to remove",
        };
        if (summary.Replace > 0)
            parts.Add($"{summary.Replace} to replace");
        parts.Add($"{summary.Run} to run");
        if (summary.Unsupported > 0)
            parts.Add($"{summary.Unsupported} deferred");
        var text = $"Plan: {string.Join(", ", parts)}.";
        return plan.Problems.Count > 0 ? $"{text} [red]{plan.Problems.Count} problem(s).[/]" : text;
    }

    static string Marker(Change change) => $"[{change.Marker.Color()}]{Markup.Escape(change.Marker.Symbol())}[/]";

    static void Line(IAnsiConsole console, string markup) => CliOutput.WriteUnwrappedLine(console, markup);
}