using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Budget;

public sealed record BudgetReport(
    string Method,
    AlwaysLoaded AlwaysLoaded,
    IReadOnlyList<ContextualFile> Contextual,
    OnDemand OnDemand,
    PushBudget Push,
    IReadOnlyList<EntryCost> Largest,
    IReadOnlyList<string> Notes);

public sealed record AlwaysLoaded(
    int Total,
    ClaudeMdCost ClaudeMd,
    IReadOnlyList<ImportedFile> Imports,
    IReadOnlyList<ExternalImport> External,
    IReadOnlyList<SkippedImport> Skipped);

public sealed record ClaudeMdCost(string Path, bool Exists, int OutsideBlock, int Block, int? BlockIfRendered, bool? Stale);

public sealed record ContextualFile(string Path, int Tokens, int ImportTokens);

public sealed record OnDemand(int Total, int Entries, IReadOnlyList<FeatureCost> ByFeature, IReadOnlyList<KindCost> ByKind);

public sealed record FeatureCost(string Feature, int Entries, int SummaryTokens, int BodyTokens, int Tokens);

public sealed record KindCost(string Kind, RenderMode? Render, PushPolicy? Push, int Entries, int SummaryTokens, int BodyTokens, int Tokens);

public sealed record PushBudget(int Total, IReadOnlyList<PushCost> ByPolicy);

public sealed record PushCost(PushPolicy Policy, int Entries, int Full, int SummaryOnly, int Tokens);

public sealed record EntryCost(string Address, string Kind, int Tokens);
