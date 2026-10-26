namespace RepoCli.Core.Features.Anchors;

/// <summary>The resolver step that located an anchor.</summary>
public enum AnchorSearch
{
    None,
    Hint,
    SameFile,

    /// <summary>The file was renamed; the anchor was then found in the new file by context or symbol.</summary>
    Rename,
    Symbol,

    /// <summary>Similar but not identical context; always needs confirmation.</summary>
    Fuzzy
}
