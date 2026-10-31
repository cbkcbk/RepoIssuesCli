using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Graph;
using Spectre.Console;

namespace RepoInfraCli.Cli;

public static class GraphOutline
{
    public static void Write(IAnsiConsole console, InfraGraph graph)
    {
        Line(console, $"[bold]Project[/] {Markup.Escape(graph.Project.Name)}" + (graph.Env is null ? "" : $" [grey]env[/] {Markup.Escape(graph.Env)}"));

        Line(console, $"[bold]Resources[/] ({graph.Resources.Count})");
        foreach (var resource in graph.Resources)
            Line(console, $"  {Markup.Escape(resource.Provider)} {Markup.Escape(resource.Kind)} [bold]{Markup.Escape(resource.Name)}[/]{Flags(resource)}");

        Line(console, $"[bold]Hosts[/] ({graph.Hosts.Count})");
        foreach (var host in graph.Hosts)
            Line(console, $"  [bold]{Markup.Escape(host.Name)}[/] " + (host.External ? "[grey](external)[/]" : Markup.Escape($"{host.User}@{host.Address.Display()}")));

        Line(console, $"[bold]Plays[/] ({graph.Plays.Count})");
        foreach (var play in graph.Plays)
        {
            Line(console, $"  [bold]{Markup.Escape(play.Name)}[/]");
            foreach (var group in play.Runs.GroupBy(run => run.Host))
            {
                Line(console, $"    {Markup.Escape(graph.Host(group.Key)?.Name ?? group.Key.Value)}");
                foreach (var run in group)
                    Line(console, $"      {Markup.Escape(run.Role)}: " + (run.External ? "[grey](external)[/]" : $"{run.Tasks.Count} task(s)"));
            }
        }

        if (graph.Apps.Count > 0)
            Line(console, $"[bold]Apps[/] ({graph.Apps.Count}): {Markup.Escape(string.Join(", ", graph.Apps.Select(app => $"{app.Name} ({app.Build.Id})")))}");

        var summary = GraphSummary.From(graph);
        Line(console, $"[grey]{summary.Resources} resource(s), {summary.Hosts} host(s), {summary.Plays} play(s), {summary.Apps} app(s), {summary.Tasks} task(s), {summary.Secrets} secret(s)[/]");
    }

    static string Flags(ResourceNode resource) =>
        (resource.Existing ? " [grey]existing[/]" : "") + (resource.Protect ? " [yellow]protect[/]" : "") + (resource.Exported ? " [blue]exported[/]" : "");

    static void Line(IAnsiConsole console, string markup) => CliOutput.WriteUnwrappedLine(console, markup);
}
