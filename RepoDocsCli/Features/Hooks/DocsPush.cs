using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;

namespace RepoDocsCli.Features.Hooks;

/// <param name="Full">The whole entry is pushed (short or no body), which counts as a read.</param>
public sealed record PushedEntry(EntryView View, bool Full)
{
    public string Key => $"{View.Address}@{(View.Updated ?? View.Created)?.ToUnixTimeSeconds()}";
}

/// <summary>The entries a Read or Edit hook pushes for one file, by each kind's push policy.</summary>
public static class DocsPush
{
    public const int MaxEntries = 10;
    static readonly ForOptions EveryKind = new(AllKinds: true);

    public static IReadOnlyList<PushedEntry> For(DocsSnapshot snapshot, string path, HookEvent hookEvent, string root)
    {
        var result = DocsFor.Query(snapshot, path, EveryKind, new PlanLookup(root));
        var active = result.Features.Where(feature => feature.Status == FeatureStatus.InProgress).Select(feature => feature.Feature).ToHashSet(StringComparer.Ordinal);

        return result.Entries
            .Where(view => view.Match != EntryMatch.Repo || PathMatch.Matches(view.When, path))
            .Where(view => Pushes(snapshot.Config.Kinds.GetValueOrDefault(view.Kind)?.Push ?? PushPolicy.Never, hookEvent, active.Contains(view.Feature)))
            .Select(view => new PushedEntry(view, view.Body is not null || !view.HasBody))
            .ToList();
    }

    public static bool Pushes(PushPolicy policy, HookEvent hookEvent, bool featureActive) => policy switch
    {
        PushPolicy.OnRead => hookEvent is HookEvent.Read or HookEvent.Edit,
        PushPolicy.OnEdit => hookEvent is HookEvent.Edit,
        PushPolicy.WhenFeatureActive => featureActive && hookEvent is HookEvent.Read or HookEvent.Edit,
        _ => false,
    };

    public static string Context(string path, IReadOnlyList<PushedEntry> shown, int more)
    {
        var lines = new List<string> { $"Docs for {path} (from .repo-docs; change them only with the docs CLI, never by editing the YAML):" };
        foreach (var entry in shown)
        {
            var view = entry.View;
            lines.Add("");
            lines.Add($"{view.Address} ({view.Kind}): {view.Summary}");
            if (view.Body is { } body)
                lines.Add(body.TrimEnd());
            else if (view.HasBody)
                lines.Add($"Read the rest with `docs show {view.Address}`.");
        }

        if (more > 0)
        {
            lines.Add("");
            lines.Add($"{more} more: `docs for {path}`.");
        }

        return string.Join('\n', lines);
    }
}
