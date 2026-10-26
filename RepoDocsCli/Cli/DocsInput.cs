using RepoCli.Core.Features.Anchors;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Cli;

public sealed class DocsInput(string root)
{
    public const string StandardInput = "-";

    public static void RequireInitialized(DocsStore store)
    {
        if (!store.IsInitialized)
            throw new UsageException($"No {RepoLocator.DocsFolderName} folder in {store.Root}; run 'docs init' first.");
    }

    public static void RequireKind(DocsConfig config, string kind)
    {
        if (!config.Declares(kind))
            throw new UsageException($"Kind '{kind}' is not declared in config.yaml. Declared kinds: {string.Join(", ", config.Kinds.Keys)}.");
    }

    public static DocsAddress ChooseTarget(Interaction interaction, DocsSnapshot snapshot, string? value, string title)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return DocsAddress.Parse(value);

        interaction.EnsureInteractive("[target]");
        var choices = snapshot.Features
            .SelectMany(feature => feature.Entries.Select(entry => new DocsAddress(feature.Id, entry.Id)).Prepend(new DocsAddress(feature.Id)))
            .Concat(snapshot.Repo.Entries.Select(entry => new DocsAddress(FeatureId.RepoFileStem, entry.Id)))
            .ToList();
        if (choices.Count == 0)
            throw new UsageException("There are no features or entries yet.");

        return interaction.Choose(title, choices, address => address.ToString(), "[target]");
    }

    public WhenHints Hints(IEnumerable<string> paths, IEnumerable<string> anchors, IEnumerable<string> keywords) => new()
    {
        Paths = paths.Select(ParsePath).Distinct().ToList(),
        Anchors = anchors.Select(ParseAnchor).Distinct().ToList(),
        Keywords = ParseKeywords(keywords).ToList(),
    };

    public string ParsePath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new UsageException("A path hint cannot be empty.");

        var full = FullPath(trimmed);
        var relative = RepoRelative(full, value);
        if (relative == ".")
            throw new UsageException($"Path hint '{value}' is the repository root; leave out the hint instead.");

        var isFolder = trimmed.EndsWith('/') || trimmed.EndsWith('\\') || Directory.Exists(full);
        return isFolder ? relative.TrimEnd('/') + "/" : relative;
    }

    public Anchor ParseAnchor(string value)
    {
        if (!TrySplitLines(value, out var path, out var line, out var endLine))
            throw new UsageException($"Invalid anchor '{value}'. Use path:line or path:line-end.");

        var full = FullPath(path);
        var relative = RepoRelative(full, value);
        if (!File.Exists(full))
            throw new UsageException($"Cannot anchor '{value}': file '{relative}' not found.");

        var text = SourceText.Load(full);
        var end = endLine ?? line;
        if (line < 1 || end < line || end > text.LineCount)
            throw new UsageException($"Cannot anchor '{value}': '{relative}' has {text.LineCount} lines.");

        return Anchor.CreateWithSymbol(relative, text, line, endLine);
    }

    public bool AnchorMatches(Anchor anchor, string value)
    {
        var trimmed = value.Trim();
        if (anchor.Symbol == trimmed)
            return true;
        if (TrySplitLines(trimmed, out var path, out var line, out _))
            return anchor.Line == line && SamePath(anchor.Path, path);
        return SamePath(anchor.Path, trimmed);
    }

    public bool PathMatches(string hint, string value)
    {
        try
        {
            return hint == value.Trim() || hint == ParsePath(value);
        }
        catch (UsageException)
        {
            return false;
        }
    }

    public static IEnumerable<string> ParseKeywords(IEnumerable<string> values) =>
        values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static PlanStep ParseStep(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var issues = new List<int>();
        while (tokens.Count > 0 && TryParseIssueToken(tokens[^1], out var numbers))
        {
            issues.InsertRange(0, numbers);
            tokens.RemoveAt(tokens.Count - 1);
        }

        return new PlanStep { Title = string.Join(' ', tokens), Issues = issues.Distinct().ToList() };
    }

    public static string FormatStep(PlanStep step) =>
        string.Join(' ', [step.Title, .. step.Issues.Select(number => $"#{number}")]);

    public static List<int> ParseIssues(string value) =>
        value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token.TrimStart('#'), out var number) && number > 0
                ? number
                : throw new UsageException($"'{token}' is not an issue number."))
            .Distinct()
            .ToList();

    public static (int Index, string Value) ParseIndexed(string value, string option, int count)
    {
        var separator = value.IndexOf('=');
        var indexText = separator < 0 ? value : value[..separator];
        if (!int.TryParse(indexText.Trim(), out var index))
            throw new UsageException($"Invalid {option} '{value}'. Start it with a step number, as in 2=....");
        if (index < 1 || index > count)
            throw new UsageException($"{option}: there is no step {index}; the plan has {count} steps.");
        return (index - 1, separator < 0 ? "" : value[(separator + 1)..]);
    }

    public static string? ReadBody(string? body, string? bodyFile, bool fromStandardInput)
    {
        if (new[] { body is not null, bodyFile is not null, fromStandardInput }.Count(set => set) > 1)
            throw new UsageException("--body, --body-file and --body-stdin cannot be combined.");

        var text = fromStandardInput || bodyFile == StandardInput ? System.Console.In.ReadToEnd()
            : bodyFile is null ? body
            : File.Exists(bodyFile) ? File.ReadAllText(bodyFile)
            : throw new UsageException($"Body file '{bodyFile}' not found.");
        return NormalizeBody(text);
    }

    public static string? NormalizeBody(string? text)
    {
        var normalized = text?.ReplaceLineEndings("\n").TrimEnd().TrimStart('\n');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    bool SamePath(string hint, string value)
    {
        try
        {
            return hint == value || hint == RepoRelative(FullPath(value), value);
        }
        catch (UsageException)
        {
            return false;
        }
    }

    static bool TrySplitLines(string value, out string path, out int line, out int? endLine)
    {
        path = "";
        line = 0;
        endLine = null;
        var separator = value.LastIndexOf(':');
        if (separator <= 0)
            return false;

        var lines = value[(separator + 1)..].Split('-');
        if (lines.Length > 2 || !int.TryParse(lines[0], out line))
            return false;
        if (lines.Length == 2)
        {
            if (!int.TryParse(lines[1], out var end))
                return false;
            endLine = end;
        }

        path = value[..separator].Trim();
        return path.Length > 0;
    }

    static bool TryParseIssueToken(string token, out List<int> numbers)
    {
        numbers = [];
        foreach (var part in token.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith('#') || !int.TryParse(part[1..], out var number) || number < 1)
                return false;
            numbers.Add(number);
        }

        return numbers.Count > 0;
    }

    string FullPath(string path) => Path.GetFullPath(path, BaseDirectory());

    string RepoRelative(string fullPath, string value)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new UsageException($"'{value}' is outside the repository.");
        return relative.Replace('\\', '/');
    }

    string BaseDirectory()
    {
        var current = Path.GetFullPath(Environment.CurrentDirectory);
        var relative = Path.GetRelativePath(root, current);
        var insideRepo = relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathRooted(relative);
        return insideRepo ? current : root;
    }
}
