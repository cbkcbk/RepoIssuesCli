namespace RepoCli.Core.Features.Anchors;

public enum AnchorState
{
    /// <summary>Context and body match at the hinted path and line.</summary>
    Ok,

    /// <summary>Found elsewhere with an identical body.</summary>
    Moved,

    /// <summary>Found, but the body hash differs.</summary>
    Changed,

    /// <summary>Not found.</summary>
    Broken
}
