namespace RepoCli.Core.Features.Anchors;

public sealed class AnchorResolver(string repoRoot)
{
    public AnchorResolution Resolve(Anchor anchor) => Batch().Resolve(anchor);

    public IReadOnlyList<AnchorResolution> ResolveAll(IEnumerable<Anchor> anchors)
    {
        var batch = Batch();
        return [.. anchors.Select(batch.Resolve)];
    }

    /// <summary>A resolver that caches files, symbols, renames and git history until it is discarded.</summary>
    public AnchorBatch Batch() => new(repoRoot);

    /// <summary>The rewritten anchor for confident moves; null for anything that needs a human.</summary>
    public static Anchor? Fix(AnchorResolution resolution) =>
        resolution is { State: AnchorState.Moved, NeedsConfirmation: false } ? resolution.Resolved : null;
}
