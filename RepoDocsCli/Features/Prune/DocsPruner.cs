using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Check;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Prune;

public static class DocsPruner
{
    sealed record Draft(string Address, string? Kind, string? Summary)
    {
        public List<PruneReason> Reasons { get; } = [];
    }

    sealed class Drafts
    {
        readonly Dictionary<string, Draft> byAddress = new(StringComparer.Ordinal);

        public IEnumerable<Draft> All => byAddress.Values;

        public void Add(string address, string? kind, string? summary, PruneCode code, string message)
        {
            if (!byAddress.TryGetValue(address, out var draft))
                byAddress[address] = draft = new Draft(address, kind, summary);
            draft.Reasons.Add(new PruneReason(code, message));
        }

        public void Add(string owner, FeatureEntry entry, PruneCode code, string message) =>
            Add(EntryId.Address(owner, entry.Id ?? "?"), entry.Kind, entry.Summary, code, message);
    }

    public static IReadOnlyList<PruneCandidate> Prune(string root, DocsSnapshot snapshot, DateOnly today, PruneFilter filter)
    {
        var inspection = DocsInspection.Run(root, snapshot);
        var drafts = new Drafts();

        foreach (var site in inspection.Sites)
            AddSite(drafts, snapshot, site);

        foreach (var plan in inspection.Plans)
            AddPlan(drafts, plan);

        AddUnread(drafts, snapshot.Config, FeatureId.RepoFileStem, snapshot.Repo.Entries, today);
        foreach (var feature in snapshot.Features)
            AddUnread(drafts, snapshot.Config, feature.Id, feature.Entries, today);

        return drafts.All
            .Select(Candidate)
            .Where(filter.Includes)
            .OrderBy(candidate => candidate.Strongest)
            .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
            .ToList();
    }

    static void AddSite(Drafts drafts, DocsSnapshot snapshot, SiteInspection inspection)
    {
        var (site, deadPaths, anchors) = inspection;
        var feature = snapshot.Find(site.Owner);
        var entry = site.EntryIndex is { } index ? Entries(snapshot, site.Owner)[index] : null;
        void Add(PruneCode code, string message) => drafts.Add(site.Address, entry?.Kind, entry is null ? feature?.Summary : entry.Summary, code, message);

        if (inspection.IsOrphaned)
        {
            if (entry is null && feature?.Status == FeatureStatus.Deprecated)
                Add(PruneCode.DeprecatedOrphan, "The feature is deprecated and none of its paths or anchors match a file any more.");
            else
                Add(PruneCode.Orphaned, "None of its when paths or anchors match a file any more.");
        }

        foreach (var path in deadPaths)
            Add(PruneCode.DeadPath, $"Path '{path}' matches no file or folder.");

        foreach (var resolution in anchors)
        {
            if (resolution.State == AnchorState.Broken)
                Add(PruneCode.BrokenAnchor, $"Anchor {Location(resolution.Anchor)} is broken ({resolution.Reason}).");
            else if (resolution.State == AnchorState.Changed)
                Add(PruneCode.ChangedAnchor, $"Anchor {Location(resolution.Anchor)} changed at {Location(resolution.Resolved ?? resolution.Anchor)} ({resolution.Reason}).");
        }
    }

    static void AddPlan(Drafts drafts, PlanInspection plan)
    {
        if (plan.Status == FeatureStatus.Shipped)
            drafts.Add(plan.Owner, plan.Entry, PruneCode.ShippedPlan, $"Feature '{plan.Owner}' is shipped; convert the plan to a decision or fact, or delete it.");

        var done = plan.Progress.Steps.Where(step => step.State == StepState.Done).ToList();
        if (done.Count > 0)
            drafts.Add(plan.Owner, plan.Entry, PruneCode.CollapsibleStep,
                $"{done.Count} of {plan.Progress.Steps.Count} step(s) have all issues done: {string.Join(", ", done.Select(step => $"'{step.Title}'"))}.");
    }

    static void AddUnread(Drafts drafts, DocsConfig config, string owner, IReadOnlyList<FeatureEntry> entries, DateOnly today)
    {
        foreach (var entry in entries)
        {
            if (config.Kinds.GetValueOrDefault(entry.Kind ?? "")?.Prune?.UnreadDays is not { } limit || LastSeen(entry) is not { } seen)
                continue;

            var days = today.DayNumber - seen.DayNumber;
            if (days < limit)
                continue;

            var never = entry.Read is null ? ", never read" : "";
            drafts.Add(owner, entry, PruneCode.Unread, $"Unread for {days} days (last read or change {seen:yyyy-MM-dd}{never}); '{entry.Kind}' entries are flagged after {limit}.");
        }
    }

    static DateOnly? LastSeen(FeatureEntry entry) =>
        new[] { entry.Read, DateOf(entry.Updated), DateOf(entry.Created) }.Max();

    static DateOnly? DateOf(DateTimeOffset? value) => value is { } time ? DateOnly.FromDateTime(time.UtcDateTime) : null;

    static IReadOnlyList<FeatureEntry> Entries(DocsSnapshot snapshot, string owner) =>
        owner == FeatureId.RepoFileStem ? snapshot.Repo.Entries : snapshot.Find(owner)?.Entries ?? [];

    static PruneCandidate Candidate(Draft draft)
    {
        var reasons = draft.Reasons.OrderBy(reason => reason.Code).ToList();
        return new PruneCandidate(draft.Address, draft.Kind, draft.Summary, reasons, Suggest(reasons[0].Code, draft.Address));
    }

    static PruneSuggestion Suggest(PruneCode code, string address) => code switch
    {
        PruneCode.DeprecatedOrphan or PruneCode.Orphaned => new("delete", $"docs rm {address}"),
        PruneCode.BrokenAnchor or PruneCode.DeadPath => new("re-anchor", $"docs edit {address} ..."),
        PruneCode.ChangedAnchor => new("review", $"docs edit {address} ..."),
        PruneCode.ShippedPlan => new("convert-to-decision", $"docs edit {address} ..."),
        PruneCode.Unread => new("review", $"docs touch {address}"),
        _ => new("collapse-steps", $"docs edit {address} ..."),
    };

    static string Location(Anchor anchor) => $"{anchor.Path}:{anchor.Line}";
}
