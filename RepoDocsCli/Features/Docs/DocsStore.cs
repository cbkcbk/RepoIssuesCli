using YamlDotNet.Core;

namespace RepoDocsCli.Features.Docs;

public sealed record DocsFileError(string FileName, string Message);

public sealed record DocsSnapshot(RepoDocs Repo, DocsConfig Config, IReadOnlyList<Feature> Features, IReadOnlyList<DocsFileError> Errors)
{
    public Feature? Find(string id) => Features.FirstOrDefault(feature => feature.Id == id);
}

public sealed class FeatureNotFoundException(string id) : NotFoundException($"Feature '{id}' not found.");

public sealed class DocsFileException(DocsFileError error) : Exception($"{error.FileName}: {error.Message}")
{
    public DocsFileError Error => error;
}

public sealed class DocsStore(string root)
{
    public const string Extension = ".yaml";
    const string IgnoreFileName = ".gitignore";

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string Root => root;

    public string Folder => Path.Combine(root, RepoLocator.DocsFolderName);

    public string RepoFile => PathOf(FeatureId.RepoFileStem);

    public string ConfigFile => PathOf(FeatureId.ConfigFileStem);

    public bool IsInitialized => Directory.Exists(Folder);

    public static DocsStore Open(string? explicitRoot) =>
        new(RepoLocator.FindRoot(explicitRoot, Environment.CurrentDirectory));

    public InitResult Initialize()
    {
        var created = !IsInitialized;
        var ignoreFile = Path.Combine(Folder, IgnoreFileName);
        var hadIgnoreFile = File.Exists(ignoreFile);
        using var lease = Lock();

        var files = new List<string>();
        if (!hadIgnoreFile)
        {
            Git.Add(root, ignoreFile);
            files.Add(ignoreFile);
        }

        if (!File.Exists(RepoFile))
        {
            SaveRepo(new RepoDocs());
            files.Add(RepoFile);
        }

        if (!File.Exists(ConfigFile))
        {
            SaveConfig(DocsConfig.Default());
            files.Add(ConfigFile);
        }

        var features = created ? FeatureFolders.Prefill(root) : [];
        foreach (var feature in features)
        {
            Save(feature);
            files.Add(PathFor(feature.Id));
        }

        return new InitResult(Folder, created, files.Select(RepoPath).ToList(), features);
    }

    public IDisposable Lock() => FolderLock.Acquire(Folder, LockTimeout);

    public DocsSnapshot Load()
    {
        if (!IsInitialized)
            return new DocsSnapshot(new RepoDocs(), DocsConfig.Default(), [], []);

        var errors = new List<DocsFileError>();
        var repo = TryRead<RepoDocs>(RepoFile, errors) is { } repoDocs ? Normalize(repoDocs) : new RepoDocs();
        var config = TryRead<DocsConfig>(ConfigFile, errors) is { } docsConfig ? Normalize(docsConfig) : DocsConfig.Default();

        var features = new List<Feature>();
        foreach (var path in FeatureFiles())
        {
            try
            {
                features.Add(ReadFeature(path));
            }
            catch (DocsFileException exception)
            {
                errors.Add(exception.Error);
            }
        }

        return new DocsSnapshot(repo, config, features, errors);
    }

    public Feature LoadFeature(string id)
    {
        var path = PathFor(FeatureId.Require(id));
        return File.Exists(path) ? ReadFeature(path) : throw new FeatureNotFoundException(id);
    }

    public bool Exists(string id) => FeatureId.IsValid(id) && File.Exists(PathFor(id));

    public void Save(Feature feature) => Write(PathFor(FeatureId.Require(feature.Id)), DocsYaml.Serialize(feature));

    public void SaveRepo(RepoDocs repo) => Write(RepoFile, DocsYaml.Serialize(repo));

    public void SaveConfig(DocsConfig config) => Write(ConfigFile, DocsYaml.Serialize(config));

    public void Delete(string id)
    {
        var path = PathFor(FeatureId.Require(id));
        if (!File.Exists(path))
            throw new FeatureNotFoundException(id);

        Git.Remove(root, path);
    }

    public Feature Rename(string id, string newId)
    {
        var renamed = LoadFeature(id) with { Id = FeatureId.Require(newId) };
        if (id == newId)
            return renamed;
        if (File.Exists(PathFor(newId)))
            throw new UsageException($"Feature '{newId}' already exists.");

        Git.Move(root, PathFor(id), PathFor(newId));
        Save(renamed);
        return renamed;
    }

    public string PathFor(string id) => PathOf(id);

    IEnumerable<string> FeatureFiles() =>
        Directory.EnumerateFiles(Folder, "*" + Extension)
            .Where(path => path != RepoFile && path != ConfigFile)
            .Order(StringComparer.Ordinal);

    Feature ReadFeature(string path)
    {
        var fileName = Path.GetFileName(path);
        var feature = Read<Feature>(path) ?? throw new DocsFileException(new DocsFileError(fileName, "File is empty."));
        var expected = Path.GetFileNameWithoutExtension(path);
        if (feature.Id != expected)
            throw new DocsFileException(new DocsFileError(fileName, $"Feature id '{feature.Id}' does not match the file name; expected 'feature: {expected}'."));
        if (!FeatureId.IsValid(feature.Id))
            throw new DocsFileException(new DocsFileError(fileName, $"'{feature.Id}' is not a valid feature id."));

        return Normalize(feature);
    }

    static T? TryRead<T>(string path, List<DocsFileError> errors) where T : class
    {
        try
        {
            return File.Exists(path) ? Read<T>(path) : null;
        }
        catch (DocsFileException exception)
        {
            errors.Add(exception.Error);
            return null;
        }
    }

    static T? Read<T>(string path) where T : class
    {
        try
        {
            return DocsYaml.Deserialize<T>(File.ReadAllText(path));
        }
        catch (YamlException exception)
        {
            var message = exception.InnerException is { } inner ? $"{exception.Message} {inner.Message}" : exception.Message;
            throw new DocsFileException(new DocsFileError(Path.GetFileName(path), message));
        }
    }

    static Feature Normalize(Feature feature) => feature with
    {
        Summary = string.IsNullOrWhiteSpace(feature.Summary) ? null : feature.Summary,
        When = new FeatureWhen { Paths = feature.When?.Paths ?? [] },
        Entries = feature.Entries ?? [],
    };

    static RepoDocs Normalize(RepoDocs repo) => repo with { Entries = repo.Entries ?? [] };

    static DocsConfig Normalize(DocsConfig config) => config with
    {
        Kinds = (config.Kinds ?? []).ToDictionary(kind => kind.Key, kind => kind.Value ?? new KindConfig()),
    };

    void Write(string path, string content)
    {
        Directory.CreateDirectory(Folder);
        var isNew = !File.Exists(path);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);

        if (isNew)
            Git.Add(root, path);
    }

    string RepoPath(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    string PathOf(string stem) => Path.Combine(Folder, stem + Extension);
}
