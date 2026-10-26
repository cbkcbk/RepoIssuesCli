using System.Text;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.ClaudeMd;

public sealed record RenderResult(string Path, bool Created, bool Changed, bool Stale);

public static class ClaudeMdFile
{
    public const string FileName = "CLAUDE.md";
    const char ByteOrderMark = '﻿';

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string PathIn(string root) => Path.Combine(root, FileName);

    public static string? Read(string root)
    {
        var path = PathIn(root);
        return File.Exists(path) ? Utf8.GetString(File.ReadAllBytes(path)) : null;
    }

    public static string Apply(string? existing, string block)
    {
        if (string.IsNullOrEmpty(existing))
            return block + "\n";

        if (FindBlock(existing) is not { } found)
            return existing + Separator(existing) + block + "\n";

        return string.Concat(existing.AsSpan(0, found.Start), block, existing.AsSpan(found.End));
    }

    public static bool IsStale(string root, DocsSnapshot snapshot)
    {
        var existing = Read(root);
        return Apply(existing, ClaudeMdBlock.Render(snapshot)) != existing;
    }

    public static RenderResult Check(string root, DocsSnapshot snapshot) =>
        new(PathIn(root), Created: false, Changed: false, Stale: IsStale(root, snapshot));

    public static RenderResult Write(string root, DocsSnapshot snapshot)
    {
        var path = PathIn(root);
        var existing = Read(root);
        var updated = Apply(existing, ClaudeMdBlock.Render(snapshot));
        if (updated == existing)
            return new RenderResult(path, Created: false, Changed: false, Stale: false);

        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Utf8.GetBytes(updated));
        File.Move(temporary, path, overwrite: true);

        var created = existing is null;
        if (created)
            Git.Add(root, path);

        return new RenderResult(path, created, Changed: true, Stale: true);
    }

    static (int Start, int End)? FindBlock(string text)
    {
        var begins = MarkerLines(text, ClaudeMdBlock.BeginMarker).ToList();
        var ends = MarkerLines(text, ClaudeMdBlock.EndMarker).ToList();

        if (begins.Count == 0)
            return ends.Count == 0 ? null : throw MarkerError($"has '{ClaudeMdBlock.EndMarker}' without a '{ClaudeMdBlock.BeginMarker}' line before it");
        if (begins.Count > 1)
            throw MarkerError($"has {begins.Count} '{ClaudeMdBlock.BeginMarker}' lines; keep one generated block");

        var end = ends.FirstOrDefault(index => index > begins[0], -1);
        return end < 0
            ? throw MarkerError($"has a '{ClaudeMdBlock.BeginMarker}' line without '{ClaudeMdBlock.EndMarker}' after it")
            : (begins[0], end + ClaudeMdBlock.EndMarker.Length);
    }

    static IEnumerable<int> MarkerLines(string text, string marker)
    {
        for (var index = text.IndexOf(marker, StringComparison.Ordinal); index >= 0; index = text.IndexOf(marker, index + 1, StringComparison.Ordinal))
        {
            if (index == 0 || text[index - 1] == '\n' || (index == 1 && text[0] == ByteOrderMark))
                yield return index;
        }
    }

    static string Separator(string text) =>
        text.EndsWith("\n\n") || text.EndsWith("\r\n\r\n") ? ""
        : text.EndsWith('\n') ? "\n"
        : "\n\n";

    static DocsFileException MarkerError(string problem) =>
        new(new DocsFileError(FileName, $"{FileName} {problem}. Fix the markers by hand, then run docs render again."));
}
