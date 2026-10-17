using System.Text;
using System.Text.Json;

namespace RepoIssuesCli.Features.Issues;

public sealed record IssueFileError(string FileName, string Message);

public sealed record IssueSnapshot(IReadOnlyList<Issue> Issues, IReadOnlyList<IssueFileError> Errors)
{
    public int NextNumber() => Issues.Count == 0 ? 1 : Issues.Max(issue => issue.Number) + 1;

    public Issue? FindByNumber(int number) => Issues.FirstOrDefault(issue => issue.Number == number);
}

public sealed class IssueFileConflictException(string fileName)
    : Exception($"{fileName} already belongs to another issue. Run 'issues validate' to find duplicate numbers.");

public sealed class IssueStore(string root)
{
    const int MaxSlugLength = 60;

    readonly Dictionary<Guid, string> files = [];

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
        files.Clear();
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
                {
                    errors.Add(new IssueFileError(fileName, "File is empty."));
                    continue;
                }

                issues.Add(issue);
                files[issue.Id] = fileName;
            }
            catch (JsonException exception)
            {
                errors.Add(new IssueFileError(fileName, exception.Message));
            }
        }

        foreach (var issue in issues)
            RenameToCanonical(issue);

        return new IssueSnapshot(issues.OrderBy(issue => issue.Number).ToList(), errors);
    }

    public void Save(Issue issue)
    {
        Directory.CreateDirectory(Folder);
        var current = files.GetValueOrDefault(issue.Id);
        var target = FileNameFor(issue);
        if (current != target && File.Exists(PathOf(target)))
            throw new IssueFileConflictException(target);

        if (current is not null && current != target)
            Git.Move(root, PathOf(current), PathOf(target));

        var path = PathOf(target);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, IssueJson.Serialize(issue));
        File.Move(temporary, path, overwrite: true);
        files[issue.Id] = target;

        if (current is null)
            Git.Add(root, path);
    }

    public void Delete(Issue issue)
    {
        Git.Remove(root, PathOf(files.GetValueOrDefault(issue.Id) ?? FileNameFor(issue)));
        files.Remove(issue.Id);
    }

    public string PathFor(Issue issue) => PathOf(files.GetValueOrDefault(issue.Id) ?? FileNameFor(issue));

    public int? LineCount(string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        if (Directory.Exists(path))
            return 0;
        return File.Exists(path) ? File.ReadLines(path).Count() : null;
    }

    public static string FileNameFor(Issue issue)
    {
        var slug = Slug(issue.Name);
        return slug.Length == 0 ? $"{issue.Number:D5}.json" : $"{issue.Number:D5}-{slug}.json";
    }

    static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (var character in name.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(character))
                slug.Append(char.ToLowerInvariant(character));
            else if (slug.Length > 0 && slug[^1] != '_')
                slug.Append('_');
        }

        var text = slug.ToString().TrimEnd('_');
        if (text.Length <= MaxSlugLength)
            return text;

        var cut = text.LastIndexOf('_', MaxSlugLength);
        return cut > 0 ? text[..cut] : text[..MaxSlugLength];
    }

    void RenameToCanonical(Issue issue)
    {
        var current = files[issue.Id];
        var target = FileNameFor(issue);
        if (current == target || File.Exists(PathOf(target)) || files.ContainsValue(target))
            return;

        Git.Move(root, PathOf(current), PathOf(target));
        files[issue.Id] = target;
    }

    string PathOf(string fileName) => Path.Combine(Folder, fileName);
}
