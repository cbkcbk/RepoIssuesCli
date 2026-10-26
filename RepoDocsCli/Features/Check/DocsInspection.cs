using RepoCli.Core.Features.Anchors;
using RepoCli.Core.Features.IssueLinks;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;

namespace RepoDocsCli.Features.Check;

public sealed record HintSite(string Owner, int? EntryIndex, string Address, WhenHints When)
{
    public string File => DocsStore.FileNameFor(Owner);
}

public sealed record SiteInspection(HintSite Site, IReadOnlyList<string> DeadPaths, IReadOnlyList<AnchorResolution> Anchors)
{
    public bool IsOrphaned =>
        Site.When.Paths.Count + Site.When.Anchors.Count > 0
        && DeadPaths.Count == Site.When.Paths.Count
        && Anchors.All(anchor => anchor.State == AnchorState.Broken);
}

public sealed record PlanInspection(string Owner, FeatureStatus? Status, string Address, FeatureEntry Entry, PlanProgress Progress)
{
    public string File => DocsStore.FileNameFor(Owner);
}

public sealed record DocsInspection(DocsSnapshot Snapshot, IReadOnlyList<SiteInspection> Sites, IReadOnlyList<PlanInspection> Plans)
{
    public static DocsInspection Run(string root, DocsSnapshot snapshot)
    {
        var paths = new RepoPaths(root);
        var batch = new AnchorResolver(root).Batch();
        var sites = HintSites(snapshot)
            .Select(site => new SiteInspection(site,
                [.. site.When.Paths.Where(path => !paths.Exists(path))],
                [.. site.When.Anchors.Select(batch.Resolve)]))
            .ToList();

        var issues = IssueIndex.Load(root);
        var plans = PlanEntries(FeatureId.RepoFileStem, null, snapshot.Repo.Entries, issues)
            .Concat(snapshot.Features.SelectMany(feature => PlanEntries(feature.Id, feature.Status, feature.Entries, issues)))
            .ToList();

        return new DocsInspection(snapshot, sites, plans);
    }

    static IEnumerable<HintSite> HintSites(DocsSnapshot snapshot)
    {
        foreach (var site in EntrySites(FeatureId.RepoFileStem, snapshot.Repo.Entries))
            yield return site;

        foreach (var feature in snapshot.Features)
        {
            yield return new HintSite(feature.Id, null, feature.Id, feature.When);
            foreach (var site in EntrySites(feature.Id, feature.Entries))
                yield return site;
        }
    }

    static IEnumerable<HintSite> EntrySites(string owner, IReadOnlyList<FeatureEntry> entries) =>
        entries.Index()
            .Where(item => item.Item.When is not null)
            .Select(item => new HintSite(owner, item.Index, EntryId.Address(owner, item.Item.Id ?? "?"), item.Item.When!));

    static IEnumerable<PlanInspection> PlanEntries(string owner, FeatureStatus? status, IReadOnlyList<FeatureEntry> entries, IssueIndex issues) =>
        entries
            .Where(entry => entry.Kind == PlanStep.EntryKind)
            .Select(entry => new PlanInspection(owner, status, EntryId.Address(owner, entry.Id ?? "?"), entry, PlanProgress.For(entry, issues)));
}
