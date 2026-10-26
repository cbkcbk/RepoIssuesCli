using RepoCli.Core.Features.Text;
using RepoDocsCli.Features.ClaudeMd;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;

namespace RepoDocsCli.Features.Budget;

public static class DocsBudget
{
    public const int DefaultTop = 10;

    static readonly PushPolicy[] PushingPolicies = [PushPolicy.OnEdit, PushPolicy.OnRead, PushPolicy.WhenFeatureActive];

    public static BudgetReport Measure(string root, DocsSnapshot? snapshot, int top)
    {
        var notes = new List<string>();
        if (snapshot is null)
            notes.Add($"No {RepoLocator.DocsFolderName} folder: only CLAUDE.md, its imports and nested CLAUDE.md files are measured. Run 'docs init' to manage docs.");

        var entries = snapshot is null ? [] : Measured(snapshot).ToList();
        var config = snapshot?.Config ?? DocsConfig.Default();

        return new BudgetReport(
            TokenEstimate.Method,
            AlwaysLoaded(root, snapshot, notes),
            Contextual(root),
            OnDemand(snapshot, entries, config),
            Push(entries, config),
            entries
                .OrderByDescending(entry => entry.Tokens)
                .ThenBy(entry => entry.Address, StringComparer.Ordinal)
                .Take(top)
                .Select(entry => new EntryCost(entry.Address, entry.Entry.Kind, entry.Tokens))
                .ToList(),
            notes);
    }

    static AlwaysLoaded AlwaysLoaded(string root, DocsSnapshot? snapshot, List<string> notes)
    {
        var rendered = snapshot is null ? null : ClaudeMdBlock.Render(snapshot);
        int? renderedTokens = rendered is null ? null : TokenEstimate.Of(rendered);
        var text = ClaudeMdFile.Read(root);
        if (text is null)
        {
            notes.Add($"No {ClaudeMdFile.FileName} at the repository root.");
            var missing = new ClaudeMdCost(ClaudeMdFile.FileName, Exists: false, 0, 0, renderedTokens, rendered is null ? null : true);
            return new AlwaysLoaded(0, missing, [], [], []);
        }

        var (outside, block) = SplitBlock(text, notes);
        var chain = ClaudeMdImports.Follow(root, ClaudeMdFile.PathIn(root), text);
        var claudeMd = new ClaudeMdCost(
            ClaudeMdFile.FileName,
            Exists: true,
            TokenEstimate.Of(outside),
            TokenEstimate.Of(block),
            renderedTokens,
            rendered is null ? null : block != rendered);

        return new AlwaysLoaded(claudeMd.OutsideBlock + claudeMd.Block + chain.Tokens, claudeMd, chain.Files, chain.External, chain.Skipped);
    }

    static (string Outside, string Block) SplitBlock(string text, List<string> notes)
    {
        try
        {
            return ClaudeMdFile.Split(text);
        }
        catch (DocsFileException exception)
        {
            notes.Add($"{exception.Message} The whole file is counted as outside the block.");
            return (text, "");
        }
    }

    static List<ContextualFile> Contextual(string root) =>
        FeatureFolders.Directories(root)
            .Select(folder => Path.Combine(folder, ClaudeMdFile.FileName))
            .Where(File.Exists)
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Select(file => new ContextualFile(
                ClaudeMdImports.Display(root, file.Path),
                TokenEstimate.Of(file.Text),
                ClaudeMdImports.Follow(root, file.Path, file.Text).Tokens))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

    static OnDemand OnDemand(DocsSnapshot? snapshot, List<MeasuredEntry> entries, DocsConfig config)
    {
        IEnumerable<string> owners = snapshot is null ? [] : [FeatureId.RepoFileStem, .. snapshot.Features.Select(feature => feature.Id)];
        var byFeature = owners
            .Select(owner =>
            {
                var owned = entries.Where(entry => entry.Owner == owner).ToList();
                return new FeatureCost(owner, owned.Count, owned.Sum(entry => entry.Summary), owned.Sum(entry => entry.Body), owned.Sum(entry => entry.Tokens));
            })
            .OrderByDescending(cost => cost.Tokens)
            .ThenBy(cost => cost.Feature, StringComparer.Ordinal)
            .ToList();

        var byKind = entries
            .GroupBy(entry => entry.Entry.Kind)
            .Select(group =>
            {
                var settings = config.Kinds.GetValueOrDefault(group.Key);
                return new KindCost(group.Key, settings?.Render, settings?.Push, group.Count(), group.Sum(entry => entry.Summary), group.Sum(entry => entry.Body), group.Sum(entry => entry.Tokens));
            })
            .OrderByDescending(cost => cost.Tokens)
            .ThenBy(cost => cost.Kind, StringComparer.Ordinal)
            .ToList();

        return new OnDemand(entries.Sum(entry => entry.Tokens), entries.Count, byFeature, byKind);
    }

    static PushBudget Push(List<MeasuredEntry> entries, DocsConfig config)
    {
        var byPolicy = PushingPolicies
            .Select(policy =>
            {
                var pushed = entries.Where(entry => PolicyOf(entry, config) == policy && (policy != PushPolicy.WhenFeatureActive || entry.Active)).ToList();
                var full = pushed.Where(entry => IsShort(entry.Entry.Body)).ToList();
                return new PushCost(policy, pushed.Count, full.Count, pushed.Count - full.Count, pushed.Sum(entry => entry.Summary) + full.Sum(entry => entry.Body));
            })
            .ToList();

        return new PushBudget(byPolicy.Sum(cost => cost.Tokens), byPolicy);
    }

    static PushPolicy PolicyOf(MeasuredEntry entry, DocsConfig config) =>
        config.Kinds.TryGetValue(entry.Entry.Kind, out var settings) ? settings.Push : PushPolicy.Never;

    static bool IsShort(string? body) => body is null || body.Length <= DocsFor.ShortBodyLength;

    static IEnumerable<MeasuredEntry> Measured(DocsSnapshot snapshot) =>
        snapshot.Repo.Entries.Select(entry => MeasuredEntry.Create(FeatureId.RepoFileStem, entry, active: false))
            .Concat(snapshot.Features.SelectMany(feature => feature.Entries.Select(entry => MeasuredEntry.Create(feature.Id, entry, feature.Status == FeatureStatus.InProgress))));

    sealed record MeasuredEntry(string Owner, FeatureEntry Entry, bool Active, int Summary, int Body)
    {
        public string Address => EntryId.Address(Owner, Entry.Id);

        public int Tokens => Summary + Body;

        public static MeasuredEntry Create(string owner, FeatureEntry entry, bool active) =>
            new(owner, entry, active, TokenEstimate.Of(entry.Summary), TokenEstimate.Of(entry.Body));
    }
}
