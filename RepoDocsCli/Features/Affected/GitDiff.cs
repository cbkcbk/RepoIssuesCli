using System.Text.RegularExpressions;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Affected;

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <param name="PreviousPath">The path at the base ref, for renames.</param>
public sealed record FileChange(string Path, ChangeKind Kind, string? PreviousPath = null)
{
    public IEnumerable<string> Paths => PreviousPath is null ? [Path] : [Path, PreviousPath];
}

/// <summary>Lines changed in the old version of a file; a count of 0 is an insertion after <paramref name="Start"/>.</summary>
public sealed record LineRange(int Start, int Count)
{
    public bool Touches(int first, int last) =>
        Count == 0 ? first <= Start && Start < last : Start <= last && Start + Count - 1 >= first;
}

/// <summary>The changes between a git ref and the working tree, including staged, unstaged and untracked files.</summary>
public sealed partial class GitDiff
{
    public const string WorkingHead = "HEAD";
    static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    readonly string root;
    readonly string since;
    readonly Lazy<Dictionary<string, List<LineRange>>> hunks;
    readonly Lazy<HashSet<string>> uncommitted;
    readonly Lazy<Dictionary<string, DateTimeOffset>> commitTimes;

    GitDiff(string root, string since, IReadOnlyList<FileChange> files, IReadOnlyList<string> untracked)
    {
        this.root = root;
        this.since = since;
        Files = files;
        hunks = new(() => ParseHunks(Run("diff", "-M", "-U0", "--relative", "--no-ext-diff", "--no-color", "--src-prefix=a/", "--dst-prefix=b/", since, "--")));
        uncommitted = new(() => new HashSet<string>(since == WorkingHead
            ? files.SelectMany(file => file.Paths)
            : [.. ParseNames(Run("diff", "--name-only", "-z", "--no-renames", "--relative", WorkingHead, "--")), .. untracked], PathComparer));
        commitTimes = new(() => since == WorkingHead
            ? new Dictionary<string, DateTimeOffset>(PathComparer)
            : ParseLog(Run("log", "--no-renames", "--format=%x00%at", "--name-only", "--relative", $"{since}..{WorkingHead}", "--")));
    }

    public IReadOnlyList<FileChange> Files { get; }

    public static GitDiff Read(string root, string since)
    {
        if (string.IsNullOrWhiteSpace(since) || since.StartsWith('-'))
            throw new UsageException($"'{since}' is not a git ref.");
        if (Git.Run(root, "diff", "-M", "--name-status", "-z", "--relative", "--no-ext-diff", since, "--") is not { Succeeded: true } names)
            throw new UsageException($"Can't diff against '{since}': it must name a commit in the git repository at {root}.");

        var untracked = ParseNames(Git.Run(root, "ls-files", "--others", "--exclude-standard", "-z")?.Output);
        var files = ParseNameStatus(names.Output);
        files.AddRange(untracked.Select(path => new FileChange(path, ChangeKind.Added)));
        return new GitDiff(root, since, files, untracked);
    }

    public IReadOnlyList<LineRange> HunksOf(string previousPath) =>
        hunks.Value.TryGetValue(previousPath, out var ranges) ? ranges : [];

    public string? Show(string path) => Git.Show(root, since, Path.Combine(root, path));

    /// <summary>The last write time for uncommitted changes, otherwise the author time of the last commit since the ref.</summary>
    public DateTimeOffset ChangeTime(string path) =>
        !uncommitted.Value.Contains(path) && commitTimes.Value.TryGetValue(path, out var committed)
            ? committed
            : DocsTime.Truncate(LastWrite(Path.Combine(root, path)));

    string? Run(params string[] arguments) =>
        Git.Run(root, ["-c", "core.quotePath=false", .. arguments]) is { Succeeded: true } result ? result.Output : null;

    static DateTimeOffset LastWrite(string path)
    {
        if (File.Exists(path))
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));

        var folder = Path.GetDirectoryName(path);
        while (folder is not null && !Directory.Exists(folder))
            folder = Path.GetDirectoryName(folder);
        return folder is null ? DateTimeOffset.UtcNow : new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder));
    }

    static List<FileChange> ParseNameStatus(string output)
    {
        var fields = output.Split('\0');
        var changes = new List<FileChange>();
        for (var index = 0; index + 1 < fields.Length && fields[index].Length > 0;)
        {
            var status = fields[index][0];
            if (status is 'R' or 'C' && index + 2 < fields.Length)
            {
                changes.Add(status == 'R'
                    ? new FileChange(fields[index + 2], ChangeKind.Renamed, fields[index + 1])
                    : new FileChange(fields[index + 2], ChangeKind.Added));
                index += 3;
                continue;
            }

            changes.Add(new FileChange(fields[index + 1], status switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                _ => ChangeKind.Modified,
            }));
            index += 2;
        }

        return changes;
    }

    static List<string> ParseNames(string? output) =>
        output is null ? [] : [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];

    static Dictionary<string, List<LineRange>> ParseHunks(string? output)
    {
        var hunks = new Dictionary<string, List<LineRange>>(PathComparer);
        List<LineRange>? current = null;
        var inHeader = false;

        foreach (var line in (output ?? "").Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHeader = true;
                current = null;
            }
            else if (inHeader && line.StartsWith("--- ", StringComparison.Ordinal))
                current = OldPath(line[4..]) is { } path ? hunks.TryGetValue(path, out var known) ? known : hunks[path] = [] : null;
            else if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHeader = false;
                if (current is not null && HunkHeader().Match(line) is { Success: true } match)
                    current.Add(new LineRange(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1));
            }
        }

        return hunks;
    }

    static string? OldPath(string text)
    {
        var path = text.TrimEnd('\t', '\r');
        if (path.Length > 1 && path.StartsWith('"') && path.EndsWith('"'))
            path = path[1..^1];
        return path.StartsWith("a/", StringComparison.Ordinal) ? path[2..] : null;
    }

    static Dictionary<string, DateTimeOffset> ParseLog(string? output)
    {
        var times = new Dictionary<string, DateTimeOffset>(PathComparer);
        foreach (var commit in (output ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = commit.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !long.TryParse(lines[0], out var seconds))
                continue;

            var time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            foreach (var path in lines.Skip(1))
                if (!times.TryGetValue(path, out var known) || known < time)
                    times[path] = time;
        }

        return times;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+")]
    private static partial Regex HunkHeader();
}
