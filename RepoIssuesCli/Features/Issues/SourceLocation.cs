using System.Diagnostics.CodeAnalysis;

namespace RepoIssuesCli.Features.Issues;

public sealed record SourceLocation(string Path, int? Line = null, int? Column = null)
{
    public override string ToString() => (Line, Column) switch
    {
        (null, _) => Path,
        (_, null) => $"{Path}:{Line}",
        _ => $"{Path}:{Line}:{Column}",
    };

    public static bool TryParse(string? text, [NotNullWhen(true)] out SourceLocation? location)
    {
        location = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Trim().Split(':');
        var numbers = 0;
        while (numbers < 2 && numbers < parts.Length - 1 && IsPositiveNumber(parts[^(numbers + 1)]))
            numbers++;

        var path = string.Join(':', parts[..^numbers]);
        if (path.Length == 0)
            return false;

        location = numbers switch
        {
            2 => new SourceLocation(path, int.Parse(parts[^2]), int.Parse(parts[^1])),
            1 => new SourceLocation(path, int.Parse(parts[^1])),
            _ => new SourceLocation(path),
        };
        return true;
    }

    public SourceLocation RelativeTo(string repoRoot, string currentDirectory)
    {
        var fullPath = System.IO.Path.GetFullPath(Path, currentDirectory);
        var relative = System.IO.Path.GetRelativePath(repoRoot, fullPath);
        return this with { Path = relative.Replace('\\', '/') };
    }

    static bool IsPositiveNumber(string part) => int.TryParse(part, out var value) && value > 0;
}
