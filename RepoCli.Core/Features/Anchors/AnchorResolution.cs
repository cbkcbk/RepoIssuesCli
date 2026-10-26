namespace RepoCli.Core.Features.Anchors;

/// <param name="Resolved">The anchor re-fingerprinted at the found location; null when broken.</param>
/// <param name="Similarity">Context similarity in [0, 1] for fuzzy results.</param>
public sealed record AnchorResolution(
    AnchorState State,
    Anchor Anchor,
    Anchor? Resolved,
    AnchorSearch FoundBy,
    string Reason,
    double? Similarity = null)
{
    public bool NeedsConfirmation => FoundBy == AnchorSearch.Fuzzy;
}
