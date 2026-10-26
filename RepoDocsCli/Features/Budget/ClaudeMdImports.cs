using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RepoCli.Core.Features.Text;
using RepoDocsCli.Features.Import;

namespace RepoDocsCli.Features.Budget;

public sealed record ImportedFile(string Path, int Tokens, int Depth, string From);

public sealed record ExternalImport(string Path, string From);

public sealed record SkippedImport(string Path, string From, SkipReason Reason);

public enum SkipReason
{
    [JsonStringEnumMemberName("missing")]
    Missing,

    [JsonStringEnumMemberName("too-deep")]
    TooDeep,
}

public sealed record ImportChain(IReadOnlyList<ImportedFile> Files, IReadOnlyList<ExternalImport> External, IReadOnlyList<SkippedImport> Skipped)
{
    public int Tokens => Files.Sum(file => file.Tokens);
}

public static partial class ClaudeMdImports
{
    public const int MaxDepth = 5;

    static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ImportChain Follow(string root, string file, string text)
    {
        var files = new List<ImportedFile>();
        var external = new List<ExternalImport>();
        var skipped = new List<SkippedImport>();
        var visited = new HashSet<string>([Path.GetFullPath(file)], PathComparer);

        void Visit(string importer, string content, int depth)
        {
            var from = Display(root, importer);
            foreach (var reference in References(content))
            {
                if (IsHomePath(reference))
                {
                    external.Add(new ExternalImport(reference, from));
                    continue;
                }

                var target = Resolve(importer, reference);
                if (target is not null && visited.Contains(target))
                    continue;
                if (target is null || !File.Exists(target))
                {
                    skipped.Add(new SkippedImport(target is null ? reference : Display(root, target), from, SkipReason.Missing));
                    continue;
                }

                if (depth >= MaxDepth)
                {
                    skipped.Add(new SkippedImport(Display(root, target), from, SkipReason.TooDeep));
                    continue;
                }

                visited.Add(target);
                var imported = File.ReadAllText(target);
                files.Add(new ImportedFile(Display(root, target), TokenEstimate.Of(imported), depth + 1, from));
                Visit(target, imported, depth + 1);
            }
        }

        Visit(Path.GetFullPath(file), text, 0);
        return new ImportChain(files, external, skipped);
    }

    public static IEnumerable<string> References(string text)
    {
        string? fence = null;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var opened = fence;
            fence = MarkdownReader.NextFence(fence, line);
            if (opened is not null || fence is not null)
                continue;

            foreach (Match match in ReferencePattern().Matches(CodeSpanPattern().Replace(line, " ")))
            {
                if (match.Groups[1].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'') is { Length: > 0 } reference)
                    yield return reference;
            }
        }
    }

    public static string Display(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var outside = Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return outside ? path : relative.Replace('\\', '/');
    }

    static bool IsHomePath(string reference) => reference == "~" || reference.StartsWith("~/", StringComparison.Ordinal) || reference.StartsWith(@"~\", StringComparison.Ordinal);

    static string? Resolve(string importer, string reference)
    {
        try
        {
            return Path.GetFullPath(reference, Path.GetDirectoryName(importer)!);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<!\S)@(\S+)")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"(`+).*?\1")]
    private static partial Regex CodeSpanPattern();
}
