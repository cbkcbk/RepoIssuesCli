using System.Text.RegularExpressions;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Postgres;

namespace RepoInfraCli.Features.Infra.Validation;

public static partial class GraphValidator
{
    public static IReadOnlyList<InfraDiagnostic> Validate(InfraGraph graph) =>
    [
        .. Names(graph),
        .. Duplicates(graph.Resources, resource => resource.Id.Value, resource => $"{resource.Kind} \"{resource.Name}\"", resource => resource.Location),
        .. Duplicates(graph.Hosts, host => host.Id.Value, host => $"host \"{host.Name}\"", host => host.Location),
        .. Duplicates(graph.Plays, play => play.Name, play => $"play \"{play.Name}\"", play => play.Location),
        .. Duplicates(graph.Apps, app => app.Name, app => $"app \"{app.Name}\"", app => app.Location),
        .. NamePrefixKinds(graph),
        .. PlayReferences(graph),
        .. PostgresValidator.Validate(graph),
    ];

    static IEnumerable<InfraDiagnostic> Names(InfraGraph graph)
    {
        if (!OwnerName().IsMatch(graph.Project.Name))
            yield return InfraDiagnostic.Error(DiagnosticCodes.InvalidName,
                $"project name \"{graph.Project.Name}\" must be lowercase letters, digits and dashes; it is part of every ownership marker", graph.Project.Location);
        if (graph.Env is { } env && !OwnerName().IsMatch(env))
            yield return InfraDiagnostic.Error(DiagnosticCodes.InvalidName, $"env name \"{env}\" must be lowercase letters, digits and dashes; it is part of every ownership marker");
        if (graph.Project.Name.Contains(Marker.NameSeparator))
            yield return InfraDiagnostic.Error(DiagnosticCodes.NameSeparator, SeparatorMessage($"project name \"{graph.Project.Name}\""), graph.Project.Location);
        if (graph.Env is { } separated && separated.Contains(Marker.NameSeparator))
            yield return InfraDiagnostic.Error(DiagnosticCodes.NameSeparator, SeparatorMessage($"env name \"{separated}\""));
        foreach (var resource in graph.Resources.Where(resource => resource.UsesNamePrefix && !resource.Existing && resource.Name.Contains(Marker.NameSeparator)))
            yield return InfraDiagnostic.Error(DiagnosticCodes.NameSeparator, SeparatorMessage($"{resource.Kind} name \"{resource.Name}\""), resource.Location);
    }

    static string SeparatorMessage(string what) =>
        $"{what} can't contain \"{Marker.NameSeparator}\"; it separates project, env and name in the live names of firewalls, SSH keys and registries";

    static IEnumerable<InfraDiagnostic> Duplicates<T>(IEnumerable<T> nodes, Func<T, string> key, Func<T, string> describe, Func<T, SourceLocation?> location)
    {
        var first = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (first.TryAdd(key(node), node))
                continue;
            var original = location(first[key(node)]);
            yield return InfraDiagnostic.Error(DiagnosticCodes.DuplicateName,
                $"{describe(node)} is declared twice" + (original is null ? "" : $" (first at {original})"), location(node));
        }
    }

    static IEnumerable<InfraDiagnostic> NamePrefixKinds(InfraGraph graph)
    {
        foreach (var resource in graph.Resources.Where(resource => resource.UsesNamePrefix && (resource.Protect || resource.Exported)))
        {
            var flag = resource.Protect ? "protect" : "exported";
            yield return InfraDiagnostic.Error(DiagnosticCodes.ProtectUnsupported,
                $"{resource.Kind} \"{resource.Name}\" can't be {flag}: a {resource.Kind} has no metadata, so it is owned by its name prefix ({graph.Marker.PrefixedName(resource.Name)}) and can't carry the {flag} marker",
                resource.Location);
        }
    }

    static IEnumerable<InfraDiagnostic> PlayReferences(InfraGraph graph)
    {
        var hosts = graph.Hosts.Select(host => host.Id).ToHashSet();
        var apps = graph.Apps.Select(app => app.Id).ToHashSet();
        foreach (var play in graph.Plays)
        {
            if (play.Hosts.Count == 0)
                yield return InfraDiagnostic.Error(DiagnosticCodes.UndefinedHost, $"play \"{play.Name}\" has no hosts", play.Location);
            foreach (var host in play.Hosts.Where(host => !hosts.Contains(host)))
                yield return InfraDiagnostic.Error(DiagnosticCodes.UndefinedHost, $"play \"{play.Name}\" targets {host}, which is not defined", play.Location);
            foreach (var app in play.Apps.Where(app => !apps.Contains(app)))
                yield return InfraDiagnostic.Error(DiagnosticCodes.UndefinedHost, $"play \"{play.Name}\" deploys {app}, which is not a valid infra.app", play.Location);
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex OwnerName();
}