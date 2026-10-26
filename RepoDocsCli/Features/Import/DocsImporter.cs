using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Import;

public sealed record ImportedEntry(string Address, string Kind, string Summary, string Source, int Line);

public sealed record ImportReference(string Source, int Line, string Text);

public sealed record ImportResult(
    bool DryRun,
    IReadOnlyList<string> Sources,
    IReadOnlyList<ImportedEntry> Imported,
    IReadOnlyList<ImportedEntry> Skipped,
    IReadOnlyList<string> CreatedFeatures,
    IReadOnlyList<ImportReference> Imports);

public sealed record ImportPlan(ImportResult Result, RepoDocs? Repo, IReadOnlyList<Feature> Features)
{
    public void Save(DocsStore store)
    {
        if (Repo is not null)
            store.SaveRepo(Repo);
        foreach (var feature in Features)
            store.Save(feature);
    }
}

public sealed class DocsImporter(string root, DocsSnapshot snapshot, DateTimeOffset now)
{
    const string RepoOwner = FeatureId.RepoFileStem;

    readonly List<FeatureEntry> repoEntries = [.. snapshot.Repo.Entries];
    readonly Dictionary<string, Feature> features = snapshot.Features.ToDictionary(
        feature => feature.Id,
        feature => feature with { Entries = [.. feature.Entries], When = feature.When with { Paths = [.. feature.When.Paths] } });
    readonly HashSet<string> changed = [];
    readonly List<string> created = [];
    readonly List<ImportedEntry> imported = [];
    readonly List<ImportedEntry> skipped = [];
    readonly List<ImportReference> references = [];

    public ImportPlan Plan(IReadOnlyList<string> sources, bool dryRun)
    {
        var repoPaths = sources.Select(source => ImportSources.RepoPath(root, source)).ToList();
        foreach (var (source, repoPath) in sources.Zip(repoPaths))
            ImportFile(File.ReadAllText(source), repoPath);

        var result = new ImportResult(dryRun, repoPaths, imported, skipped, created, references);
        var changedFeatures = changed.Where(id => id != RepoOwner).Order(StringComparer.Ordinal).Select(id => features[id]).ToList();
        return new ImportPlan(result, changed.Contains(RepoOwner) ? snapshot.Repo with { Entries = repoEntries } : null, changedFeatures);
    }

    void ImportFile(string text, string source)
    {
        var document = MarkdownReader.Read(text);
        references.AddRange(document.Imports.Select(line => new ImportReference(source, line.Number, line.Text)));

        var folder = ImportSources.FolderOf(source);
        var nestedOwner = folder.Length > 0 ? NestedOwner(folder) : null;

        foreach (var section in document.Sections)
        {
            var owner = nestedOwner ?? RootOwner(section.Headings);
            var headingIsSummary = section.Heading is not { } heading || FeatureId.FromFolderName(heading) != owner;
            foreach (var candidate in ImportSplitter.Split(section, headingIsSummary))
                Add(owner, nestedOwner is null ? null : folder, candidate, source);
        }
    }

    string RootOwner(IReadOnlyList<string> headings) =>
        headings.Reverse().Select(FeatureId.FromFolderName).FirstOrDefault(features.ContainsKey) ?? RepoOwner;

    string NestedOwner(string folder)
    {
        var match = features.Values
            .SelectMany(feature => feature.When.Paths.Select(path => (feature.Id, Prefix: path.Replace('\\', '/').TrimEnd('/') + "/")))
            .Where(candidate => candidate.Prefix.Length > 1 && folder.StartsWith(candidate.Prefix, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Prefix.Length)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (match.Id is not null)
            return match.Id;

        var segments = folder.TrimEnd('/').Split('/');
        string[] guesses = [FeatureId.FromFolderName(segments[^1]), FeatureId.FromFolderName(string.Join('-', segments))];
        return guesses.FirstOrDefault(FeatureId.IsValid) ?? RepoOwner;
    }

    void Add(string owner, string? folder, ImportCandidate candidate, string source)
    {
        var entries = EntriesOf(owner);
        if (entries.FirstOrDefault(entry => entry.Summary == candidate.Summary && Normalized(entry.Body) == Normalized(candidate.Body)) is { } duplicate)
        {
            skipped.Add(new ImportedEntry(EntryId.Address(owner, duplicate.Id), duplicate.Kind, duplicate.Summary, source, candidate.Line));
            return;
        }

        entries = Claim(owner, folder);
        var id = EntryId.Unique(EntryId.FromSummary(candidate.Summary), entries.Select(entry => entry.Id).ToHashSet());
        var kind = KindFor(candidate);
        entries.Add(new FeatureEntry { Id = id, Kind = kind, Summary = candidate.Summary, Body = candidate.Body, Created = now });
        changed.Add(owner);
        imported.Add(new ImportedEntry(EntryId.Address(owner, id), kind, candidate.Summary, source, candidate.Line));
    }

    List<FeatureEntry> EntriesOf(string owner) =>
        owner == RepoOwner ? repoEntries : features.TryGetValue(owner, out var feature) ? feature.Entries : [];

    List<FeatureEntry> Claim(string owner, string? folder)
    {
        if (owner == RepoOwner)
            return repoEntries;

        if (!features.TryGetValue(owner, out var feature))
        {
            feature = new Feature { Id = owner, Status = FeatureStatus.Shipped, When = new WhenHints { Paths = [folder!] }, Created = now };
            features[owner] = feature;
            created.Add(owner);
        }
        else if (folder is not null && !feature.When.Paths.Any(path => folder.StartsWith(path.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.Ordinal)))
        {
            feature.When.Paths.Add(folder);
            changed.Add(owner);
        }

        return feature.Entries;
    }

    string KindFor(ImportCandidate candidate)
    {
        var guess = KindGuess.Guess(candidate);
        if (snapshot.Config.Declares(guess))
            return guess;
        if (snapshot.Config.Declares(KindGuess.Fact))
            return KindGuess.Fact;
        return snapshot.Config.Kinds.Keys.FirstOrDefault()
               ?? throw new UsageException($"{DocsStore.FileNameFor(FeatureId.ConfigFileStem)} declares no kinds; add one before importing.");
    }

    static string Normalized(string? body) => (body ?? "").ReplaceLineEndings("\n").Trim();
}
