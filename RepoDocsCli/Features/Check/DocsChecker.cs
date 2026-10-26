using RepoCli.Core.Features.Anchors;
using RepoCli.Core.Features.IssueLinks;
using RepoDocsCli.Features.ClaudeMd;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Check;

public sealed record CheckReport(IReadOnlyList<ValidationProblem> Fixed, IReadOnlyList<ValidationProblem> Problems);

public static class DocsChecker
{
    sealed record HintSite(string Owner, int? EntryIndex, string Address, WhenHints When)
    {
        public string File => DocsStore.FileNameFor(Owner);
    }

    sealed record AnchorFix(HintSite Site, int Index, Anchor Anchor, ValidationProblem Problem, ValidationProblem Applied);

    sealed record Inspection(IReadOnlyList<ValidationProblem> Problems, IReadOnlyList<AnchorFix> Moves);

    public static CheckReport Check(DocsStore store) => new([], Inspect(store, store.Load()).Problems);

    public static CheckReport Fix(DocsStore store)
    {
        using var lease = store.Lock();
        var applied = new List<ValidationProblem>();

        applied.AddRange(store.ResolveConflicts().Where(conflict => conflict.Resolved).Select(conflict =>
            new ValidationProblem(ProblemKind.MergeConflict, "Merged the conflict per entry; the later timestamp wins.", Path.GetFileName(conflict.File)) { Fixable = true }));
        applied.AddRange(store.StampHandEdits().Select(path =>
            new ValidationProblem(ProblemKind.UnstampedEdit, "Stamped hand edits since HEAD.", Path.GetFileName(path)) { Fixable = true }));

        var snapshot = store.Load();
        var inspection = Inspect(store, snapshot);
        var fixedProblems = inspection.Moves.Select(move => move.Problem).ToHashSet(ReferenceEqualityComparer.Instance);
        var remaining = inspection.Problems.Where(problem => !fixedProblems.Contains(problem)).ToList();

        var updated = ApplyMoves(store, snapshot, inspection.Moves);
        applied.AddRange(inspection.Moves.Select(move => move.Applied));

        if (remaining.FirstOrDefault(problem => problem.Kind == ProblemKind.StaleClaudeMd) is { } stale)
        {
            ClaudeMdFile.Write(store.Root, updated);
            remaining.Remove(stale);
            applied.Add(stale with { Message = $"Re-rendered the generated block in {ClaudeMdFile.FileName}." });
        }

        return new CheckReport(applied, remaining);
    }

    static Inspection Inspect(DocsStore store, DocsSnapshot snapshot)
    {
        var conflicts = Conflicts(store).ToList();
        var conflicted = conflicts.Select(conflict => conflict.File).ToHashSet(StringComparer.Ordinal);
        var problems = new List<ValidationProblem>(conflicts);
        problems.AddRange(DocsValidator.Validate(snapshot).Where(problem => problem is not { Kind: ProblemKind.InvalidFile, File: { } file } || !conflicted.Contains(file)));

        var moves = new List<AnchorFix>();
        var batch = new AnchorResolver(store.Root).Batch();
        foreach (var site in HintSites(snapshot))
        {
            problems.AddRange(site.When.Paths.Where(path => !PathExists(store.Root, path)).Select(path =>
                new ValidationProblem(ProblemKind.DeadPath, $"'{site.Address}' lists path '{path}', which matches no file or folder.", site.File, site.Address)));

            foreach (var (index, anchor) in site.When.Anchors.Index())
            {
                var resolution = batch.Resolve(anchor);
                if (AnchorProblem(site, resolution) is not { } problem)
                    continue;

                problems.Add(problem);
                if (AnchorResolver.Fix(resolution) is { } moved)
                    moves.Add(new AnchorFix(site, index, moved, problem,
                        problem with { Message = $"Updated anchor {Location(anchor)} of '{site.Address}' to {Location(moved)}." }));
            }
        }

        var issues = IssueIndex.Load(store.Root);
        problems.AddRange(PlanProblems(FeatureId.RepoFileStem, null, snapshot.Repo.Entries, issues));
        foreach (var feature in snapshot.Features)
            problems.AddRange(PlanProblems(feature.Id, feature.Status, feature.Entries, issues));

        if (RenderProblem(store.Root, snapshot) is { } render && (render.Kind != ProblemKind.StaleClaudeMd || snapshot.Errors.Count == 0))
            problems.Add(render);

        return new Inspection(problems, moves);
    }

    static IEnumerable<ValidationProblem> Conflicts(DocsStore store)
    {
        if (!store.IsInitialized)
            yield break;

        foreach (var path in Directory.EnumerateFiles(store.Folder, "*" + DocsStore.Extension).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path);
            if (!ConflictMarkers.Contains(text))
                continue;

            var fileName = Path.GetFileName(path);
            var mergeable = DocsMerge.Resolve(fileName, text) is not null;
            yield return new ValidationProblem(ProblemKind.MergeConflict,
                mergeable ? "The file has git conflict markers; docs check --fix merges it per entry." : "The file has git conflict markers that can't be merged per entry; resolve them by hand.",
                fileName) { Fixable = mergeable };
        }
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

    static bool PathExists(string root, string path)
    {
        var full = Path.Combine(root, path.Replace('\\', '/').TrimEnd('/'));
        return Directory.Exists(full) || (!path.EndsWith('/') && File.Exists(full));
    }

    static ValidationProblem? AnchorProblem(HintSite site, AnchorResolution resolution)
    {
        var from = $"Anchor {Location(resolution.Anchor)} of '{site.Address}'";
        var to = resolution.Resolved is { } resolved ? Location(resolved) : "";
        ValidationProblem Problem(ProblemKind kind, string message) => new(kind, message, site.File, site.Address);

        return resolution switch
        {
            { State: AnchorState.Ok } => null,
            { State: AnchorState.Broken } => Problem(ProblemKind.BrokenAnchor, $"{from} is broken ({resolution.Reason}); re-anchor or remove it."),
            { NeedsConfirmation: true } => Problem(ProblemKind.FuzzyAnchor, $"{from} may be at {to} ({resolution.Reason}); confirm, then re-anchor it."),
            { State: AnchorState.Moved } => Problem(ProblemKind.MovedAnchor, $"{from} moved to {to} ({resolution.Reason}).") with { Fixable = true },
            _ => Problem(ProblemKind.ChangedAnchor, $"{from} changed at {to} ({resolution.Reason}); review the docs, then re-anchor it."),
        };
    }

    static string Location(Anchor anchor) => $"{anchor.Path}:{anchor.Line}";

    static IEnumerable<ValidationProblem> PlanProblems(string owner, FeatureStatus? status, IReadOnlyList<FeatureEntry> entries, IssueIndex issues)
    {
        var file = DocsStore.FileNameFor(owner);
        var linked = new List<LinkedIssue>();

        foreach (var entry in entries.Where(entry => entry.Kind == PlanStep.EntryKind && entry.Steps.Count > 0))
        {
            var progress = PlanProgress.For(entry, issues);
            var address = EntryId.Address(owner, entry.Id ?? "?");
            if (progress.Missing.Count > 0)
                yield return new ValidationProblem(ProblemKind.MissingIssue, $"Plan '{address}' links issues that are not in {RepoLocator.IssuesFolderName}: {Numbers(progress.Missing)}.", file, address);

            linked.AddRange(progress.Steps.SelectMany(step => step.Issues));
        }

        var distinct = linked.DistinctBy(issue => issue.Number).OrderBy(issue => issue.Number).ToList();
        var started = distinct.Where(issue => issue.State == LinkedIssueState.InProgress).Select(issue => issue.Number).ToList();
        var unresolved = distinct.Where(issue => issue.State is LinkedIssueState.Open or LinkedIssueState.InProgress).Select(issue => issue.Number).ToList();

        if (status == FeatureStatus.Planned && started.Count > 0)
            yield return new ValidationProblem(ProblemKind.StatusMismatch,
                $"Feature '{owner}' is planned, but these linked issues are in progress: {Numbers(started)}. Set its status to in-progress.", file, owner);

        if (status == FeatureStatus.Shipped && unresolved.Count > 0)
            yield return new ValidationProblem(ProblemKind.StatusMismatch,
                $"Feature '{owner}' is shipped, but these linked issues are still open: {Numbers(unresolved)}. Close them or change the status.", file, owner);
    }

    static string Numbers(IEnumerable<int> numbers) => string.Join(", ", numbers.Select(number => number.ToString("D4")));

    static ValidationProblem? RenderProblem(string root, DocsSnapshot snapshot)
    {
        try
        {
            return ClaudeMdFile.IsStale(root, snapshot)
                ? new ValidationProblem(ProblemKind.StaleClaudeMd, $"The generated block in {ClaudeMdFile.FileName} is missing or out of date; run docs render.", ClaudeMdFile.FileName) { Fixable = true }
                : null;
        }
        catch (DocsFileException exception)
        {
            return new ValidationProblem(ProblemKind.InvalidFile, exception.Error.Message, exception.Error.FileName);
        }
    }

    static DocsSnapshot ApplyMoves(DocsStore store, DocsSnapshot snapshot, IReadOnlyList<AnchorFix> moves)
    {
        if (moves.Count == 0)
            return snapshot;

        var byOwner = moves.ToLookup(move => move.Site.Owner);
        var repo = snapshot.Repo;
        if (byOwner.Contains(FeatureId.RepoFileStem))
        {
            repo = repo with { Entries = Apply(repo.Entries, byOwner[FeatureId.RepoFileStem]) };
            store.SaveHintFix(repo);
        }

        var features = snapshot.Features.Select(feature =>
        {
            if (!byOwner.Contains(feature.Id))
                return feature;

            var fixedFeature = feature with
            {
                When = Apply(feature.When, byOwner[feature.Id].Where(move => move.Site.EntryIndex is null)),
                Entries = Apply(feature.Entries, byOwner[feature.Id]),
            };
            store.SaveHintFix(fixedFeature);
            return fixedFeature;
        }).ToList();

        return snapshot with { Repo = repo, Features = features };
    }

    static List<FeatureEntry> Apply(List<FeatureEntry> entries, IEnumerable<AnchorFix> moves)
    {
        var byEntry = moves.Where(move => move.Site.EntryIndex is not null).ToLookup(move => move.Site.EntryIndex!.Value);
        return [.. entries.Select((entry, index) => byEntry.Contains(index) && entry.When is { } when ? entry with { When = Apply(when, byEntry[index]) } : entry)];
    }

    static WhenHints Apply(WhenHints when, IEnumerable<AnchorFix> moves)
    {
        var anchors = when.Anchors.ToList();
        foreach (var move in moves)
            anchors[move.Index] = move.Anchor;
        return when with { Anchors = anchors };
    }
}
