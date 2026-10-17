using System.Text.Json;

namespace RepoIssuesCli.Features.Issues;

public sealed record IssueFileError(string FileName, string Message);

public sealed record IssueSnapshot(IReadOnlyList<Issue> Issues, IReadOnlyList<IssueFileError> Errors)
{
    public int NextNumber() => Issues.Count == 0 ? 1 : Issues.Max(issue => issue.Number) + 1;

    public Issue? FindByNumber(int number) => Issues.FirstOrDefault(issue => issue.Number == number);
}

public sealed class IssueStore(string root)
{
    public string Root => root;

    public string Folder => Path.Combine(root, RepoLocator.IssuesFolderName);

    public bool IsInitialized => Directory.Exists(Folder);

    public static IssueStore Open(string? explicitRoot) =>
        new(RepoLocator.FindRoot(explicitRoot, Environment.CurrentDirectory));

    public bool Initialize()
    {
        if (IsInitialized)
            return false;

        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, ".gitkeep"), "");
        return true;
    }

    public IssueSnapshot Load()
    {
        if (!IsInitialized)
            return new IssueSnapshot([], []);

        var issues = new List<Issue>();
        var errors = new List<IssueFileError>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            try
            {
                var issue = IssueJson.Deserialize<Issue>(File.ReadAllText(file));
                if (issue is null)
                    errors.Add(new IssueFileError(fileName, "File is empty."));
                else if (FileNameFor(issue.Id) != fileName)
                    errors.Add(new IssueFileError(fileName, $"Id {issue.Id} does not match the file name."));
                else
                    issues.Add(issue);
            }
            catch (JsonException exception)
            {
                errors.Add(new IssueFileError(fileName, exception.Message));
            }
        }

        return new IssueSnapshot(issues.OrderBy(issue => issue.Number).ToList(), errors);
    }

    public void Save(Issue issue)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(issue.Id);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, IssueJson.Serialize(issue));
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(Issue issue) => File.Delete(PathFor(issue.Id));

    public string PathFor(Guid id) => Path.Combine(Folder, FileNameFor(id));

    static string FileNameFor(Guid id) => $"{id:D}.json";
}
