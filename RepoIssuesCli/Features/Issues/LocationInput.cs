using System.Diagnostics.CodeAnalysis;

namespace RepoIssuesCli.Features.Issues;

/// <summary>A location typed on the command line: <c>path</c>, <c>path:line</c>, <c>path:line:col</c> or <c>path:line-end</c>.</summary>
public sealed record LocationInput(string Path, int? Line = null, int? Column = null, int? EndLine = null)
{
    public override string ToString() => (Line, Column, EndLine) switch
    {
        (null, _, _) => Path,
        (_, _, { } end) => $"{Path}:{Line}-{end}",
        (_, null, _) => $"{Path}:{Line}",
        _ => $"{Path}:{Line}:{Column}",
    };

    public static bool TryParse(string? text, [NotNullWhen(true)] out LocationInput? location)
    {
        location = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split(':');
        if (parts.Length > 1 && TryParseRange(parts[^1], out var start, out var end))
        {
            location = new LocationInput(string.Join(':', parts[..^1]), start, EndLine: end);
            return location.Path.Length > 0;
        }

        var numbers = 0;
        while (numbers < 2 && numbers < parts.Length - 1 && IsPositiveNumber(parts[^(numbers + 1)]))
            numbers++;

        var path = string.Join(':', parts[..^numbers]);
        if (path.Length == 0)
            return false;

        location = numbers switch
        {
            2 => new LocationInput(path, int.Parse(parts[^2]), int.Parse(parts[^1])),
            1 => new LocationInput(path, int.Parse(parts[^1])),
            _ => new LocationInput(path),
        };
        return true;
    }

    public LocationInput RelativeTo(string repoRoot, string currentDirectory)
    {
        var fullPath = System.IO.Path.GetFullPath(Path, currentDirectory);
        var relative = System.IO.Path.GetRelativePath(repoRoot, fullPath);
        return this with { Path = relative.Replace('\\', '/') };
    }

    static bool TryParseRange(string part, out int start, out int end)
    {
        start = end = 0;
        var dash = part.IndexOf('-');
        return dash > 0
               && IsPositiveNumber(part[..dash], out start)
               && IsPositiveNumber(part[(dash + 1)..], out end)
               && end >= start;
    }

    static bool IsPositiveNumber(string part) => IsPositiveNumber(part, out _);

    static bool IsPositiveNumber(string part, out int value) => int.TryParse(part, out value) && value > 0;
}
