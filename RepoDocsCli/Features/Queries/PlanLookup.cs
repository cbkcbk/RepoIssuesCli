using RepoCli.Core.Features.IssueLinks;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public sealed class PlanLookup(string root)
{
    readonly Lazy<IssueIndex> index = new(() => IssueIndex.Load(root));

    public IssueIndex? Loaded => index.IsValueCreated ? index.Value : null;

    public PlanProgress? For(FeatureEntry entry) =>
        entry.Kind == PlanStep.EntryKind && index.Value is { IsInitialized: true } issues ? PlanProgress.For(entry, issues) : null;
}
