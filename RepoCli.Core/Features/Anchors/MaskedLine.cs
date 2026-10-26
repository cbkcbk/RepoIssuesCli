namespace RepoCli.Core.Features.Anchors;

/// <param name="Code">The line with string contents and comments replaced by spaces.</param>
/// <param name="Continued">The line starts inside a string or block comment opened on an earlier line.</param>
readonly record struct MaskedLine(string Code, bool Continued)
{
    public bool IsBlank => string.IsNullOrWhiteSpace(Code);

    public int Indent => Code.Length - Code.TrimStart().Length;
}
