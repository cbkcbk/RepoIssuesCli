using System.Text.Json.Serialization;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Prune;

public enum PruneCode
{
    DeprecatedOrphan,
    Orphaned,
    BrokenAnchor,
    ChangedAnchor,
    DeadPath,
    ShippedPlan,
    Unread,
    CollapsibleStep,
}

public sealed record PruneReason(PruneCode Code, string Message);

public sealed record PruneSuggestion(string Action, string Command);

public sealed record PruneCandidate(string Address, string? Kind, string? Summary, IReadOnlyList<PruneReason> Reasons, PruneSuggestion Suggestion)
{
    [JsonIgnore]
    public PruneCode Strongest => Reasons[0].Code;
}

public sealed record PruneFilter(IReadOnlyCollection<PruneCode> Codes, string? Feature)
{
    public static PruneFilter None { get; } = new([], null);

    public bool Includes(PruneCandidate candidate) =>
        (Codes.Count == 0 || candidate.Reasons.Any(reason => Codes.Contains(reason.Code)))
        && (Feature is null || candidate.Address == Feature || candidate.Address.StartsWith(Feature + EntryId.Separator, StringComparison.Ordinal));
}
