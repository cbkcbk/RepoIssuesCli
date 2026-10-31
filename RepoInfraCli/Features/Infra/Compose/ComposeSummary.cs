using RepoInfraCli.Features.Infra.Hosts.Tasks;

namespace RepoInfraCli.Features.Infra.Compose;

public static class ComposeSummary
{
    public static IReadOnlyList<Change>? Yaml(string current, string desired)
    {
        if (ComposeYaml.Flatten(current) is not { } before || ComposeYaml.Flatten(desired) is not { } after)
            return null;

        var changes = new List<Change>();
        var oldServices = Services(before);
        var newServices = Services(after);
        foreach (var service in oldServices.Keys.Union(newServices.Keys).Order(StringComparer.Ordinal))
        {
            var had = oldServices.TryGetValue(service, out var oldEntries);
            var has = newServices.TryGetValue(service, out var newEntries);
            if (!had)
                changes.Add(Change.Add($"service {service}" + (Value(newEntries!, "image") is { } image ? $" ({image})" : "")));
            else if (!has)
                changes.Add(Change.Remove($"service {service}"));
            else if (ServiceChange(service, oldEntries!, newEntries!) is { } change)
                changes.Add(Change.Update(change));
        }

        foreach (var section in new[] { "networks", "volumes" })
        {
            if (!Section(before, section).SetEquals(Section(after, section)))
                changes.Add(Change.Update(section));
        }
        return changes;
    }

    static Dictionary<string, List<YamlEntry>> Services(IReadOnlyList<YamlEntry> entries) =>
        entries.Where(entry => entry.Path.Count >= 3 && entry.Path[0] == "services")
            .GroupBy(entry => entry.Path[1], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    static HashSet<string> Section(IReadOnlyList<YamlEntry> entries, string section) =>
        [.. entries.Where(entry => entry.Path[0] == section).Select(entry => entry.Key + "=" + entry.Value)];

    static string? Value(List<YamlEntry> entries, string field) =>
        entries.FirstOrDefault(entry => entry.Path.Count == 3 && entry.Path[2] == field)?.Value;

    static string? ServiceChange(string service, List<YamlEntry> before, List<YamlEntry> after)
    {
        var fields = before.Concat(after).Select(entry => entry.Path[2]).Distinct().Order(StringComparer.Ordinal);
        var changed = fields.Where(field => !Field(before, field).SetEquals(Field(after, field))).ToList();
        if (changed.Count == 0)
            return null;

        var parts = new List<string>();
        if (changed.Remove("image"))
            parts.Add($"image {Value(before, "image") ?? "(none)"} -> {Value(after, "image") ?? "(none)"}");
        if (changed.Count > 0)
            parts.Add($"{string.Join(", ", changed)} changed");
        return $"service {service}: {string.Join("; ", parts)}";
    }

    static HashSet<string> Field(List<YamlEntry> entries, string field) =>
        [.. entries.Where(entry => entry.Path[2] == field).Select(entry => entry.Key + "=" + entry.Value)];

    public static string Env(string current, string desired)
    {
        var before = ComposeEnvFile.Lines(current);
        var after = ComposeEnvFile.Lines(desired);
        IEnumerable<string> names =
        [
            .. after.Keys.Where(name => !before.ContainsKey(name)).Select(name => $"+{name}"),
            .. after.Keys.Where(name => before.TryGetValue(name, out var line) && line != after[name]).Select(name => $"~{name}"),
            .. before.Keys.Where(name => !after.ContainsKey(name)).Select(name => $"-{name}"),
        ];
        return string.Join(" ", names) is { Length: > 0 } text ? text : "rewritten";
    }
}
