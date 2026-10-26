using System.Text.Json;
using RepoCli.Core.Features.Repo;

namespace RepoCli.Core.Features.IssueLinks;

public sealed record IssueIndexError(string FileName, string Message);

public sealed class IssueIndex(IReadOnlyList<IssueSummary> issues, IReadOnlyList<IssueIndexError> errors, bool isInitialized = true)
{
    readonly Dictionary<int, IssueSummary> byNumber = issues
        .GroupBy(issue => issue.Number)
        .ToDictionary(group => group.Key, group => group.First());

    public IReadOnlyList<IssueSummary> Issues => issues;

    public IReadOnlyList<IssueIndexError> Errors => errors;

    public bool IsInitialized => isInitialized;

    public IssueSummary? Find(int number) => byNumber.GetValueOrDefault(number);

    public IReadOnlyList<int> UnresolvedBlockers(IssueSummary issue) =>
        issue.BlockedBy.Where(number => Find(number) is not { } blocker || !blocker.State.IsClosed()).Distinct().ToList();

    public bool IsBlocked(IssueSummary issue) => UnresolvedBlockers(issue).Count > 0;

    public static IssueIndex Load(string root)
    {
        var folder = Path.Combine(root, RepoLocator.IssuesFolderName);
        if (!Directory.Exists(folder))
            return new IssueIndex([], [], isInitialized: false);

        var loaded = new List<IssueSummary>();
        var problems = new List<IssueIndexError>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            try
            {
                loaded.Add(Read(path, fileName));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
            {
                problems.Add(new IssueIndexError(fileName, exception.Message));
            }
        }

        return new IssueIndex(loaded.OrderBy(issue => issue.Number).ToList(), problems);
    }

    static IssueSummary Read(string path, string fileName)
    {
        var file = JsonSerializer.Deserialize(File.ReadAllText(path), IssueFileJsonContext.Default.IssueFile)
                   ?? throw new InvalidDataException("File is empty.");
        if (file.Number is not ({ } number and > 0))
            throw new InvalidDataException("Issue has no valid number.");

        var state = IssueState.Open;
        if (file.Status is { } status && (!Enum.TryParse(status, ignoreCase: true, out state) || !Enum.IsDefined(state)))
            throw new InvalidDataException($"Unknown status '{status}'.");

        return new IssueSummary(number, file.Name ?? "", state, file.BlockedBy ?? [], fileName);
    }
}
