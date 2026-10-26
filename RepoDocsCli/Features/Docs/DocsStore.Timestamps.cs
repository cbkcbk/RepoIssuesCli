using YamlDotNet.Core;

namespace RepoDocsCli.Features.Docs;

public sealed record ConflictResolution(string File, bool Resolved);

public sealed class EntryNotFoundException(string address) : NotFoundException($"Entry '{address}' not found.");

public sealed partial class DocsStore
{
    public IReadOnlyList<string> StampHandEdits(string revision = "HEAD")
    {
        if (!IsInitialized)
            return [];

        var now = DocsTime.Now(Clock);
        var stamped = FeatureFiles().Where(path => StampAgainst<Feature>(path, revision, now, Normalize, Timestamps.Stamp)).ToList();
        if (File.Exists(RepoFile) && StampAgainst<RepoDocs>(RepoFile, revision, now, Normalize, Timestamps.Stamp))
            stamped.Insert(0, RepoFile);

        return stamped.Select(RepoPath).ToList();
    }

    public IReadOnlyList<ConflictResolution> ResolveConflicts()
    {
        if (!IsInitialized)
            return [];

        var results = new List<ConflictResolution>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*" + Extension).Order(StringComparer.Ordinal).ToList())
        {
            var text = File.ReadAllText(path);
            if (!ConflictMarkers.Contains(text))
                continue;

            var merged = DocsMerge.Resolve(Path.GetFileName(path), text);
            if (merged is not null)
            {
                Write(path, merged);
                Git.Add(root, path);
            }

            results.Add(new ConflictResolution(RepoPath(path), merged is not null));
        }

        return results;
    }

    public bool MarkRead(string featureId, string entryId)
    {
        var today = DocsTime.Today(Clock);
        return UpdateEntry(featureId, entryId, entry => Timestamps.MarkRead(entry, today));
    }

    public DateTimeOffset Touch(string featureId, string? entryId = null)
    {
        var now = DocsTime.Now(Clock);
        if (entryId is not null)
            UpdateEntry(featureId, entryId, entry => Timestamps.Touch(entry, now));
        else if (featureId == FeatureId.RepoFileStem)
            throw new UsageException($"'{FeatureId.RepoFileStem}' has no timestamps of its own; touch one of its entries as {EntryId.Address(FeatureId.RepoFileStem, "<entry>")}.");
        else
            Save(Timestamps.Touch(LoadFeature(featureId), now));

        return now;
    }

    bool UpdateEntry(string featureId, string entryId, Func<FeatureEntry, FeatureEntry> change)
    {
        var address = EntryId.Address(featureId, entryId);
        if (featureId == FeatureId.RepoFileStem)
        {
            var repo = (File.Exists(RepoFile) ? Read<RepoDocs>(RepoFile) : null) is { } repoDocs ? Normalize(repoDocs) : throw new EntryNotFoundException(address);
            if (Replace(repo.Entries, entryId, address, change) is not { } repoEntries)
                return false;

            SaveRepo(repo with { Entries = repoEntries });
            return true;
        }

        var feature = LoadFeature(featureId);
        if (Replace(feature.Entries, entryId, address, change) is not { } entries)
            return false;

        Save(feature with { Entries = entries });
        return true;
    }

    static List<FeatureEntry>? Replace(List<FeatureEntry> entries, string entryId, string address, Func<FeatureEntry, FeatureEntry> change)
    {
        var index = entries.FindIndex(entry => entry.Id == entryId);
        if (index < 0)
            throw new EntryNotFoundException(address);

        var changed = change(entries[index]);
        return ReferenceEquals(changed, entries[index]) ? null : entries.Select((entry, position) => position == index ? changed : entry).ToList();
    }

    bool StampAgainst<T>(string path, string revision, DateTimeOffset now, Func<T, T> normalize, Func<T?, T, DateTimeOffset, T> stamp) where T : class
    {
        if (ReadExisting(path, normalize) is not { } current)
            return false;

        var stamped = stamp(Committed(path, revision, normalize), current, now);
        if (ReferenceEquals(stamped, current))
            return false;

        Write(path, DocsYaml.Serialize(stamped));
        return true;
    }

    T? Committed<T>(string path, string revision, Func<T, T> normalize) where T : class
    {
        try
        {
            return Git.Show(root, revision, path) is { } text && DocsYaml.Deserialize<T>(text) is { } value ? normalize(value) : null;
        }
        catch (YamlException)
        {
            return null;
        }
    }
}
