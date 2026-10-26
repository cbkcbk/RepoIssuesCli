using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoDocsCli.Features.Hooks;

/// <summary>Per-session hook state in the git-ignored <c>.repo-docs/.session/</c> folder: the starting HEAD and what was already pushed or reported.</summary>
public sealed partial class HookSession
{
    public const string FolderName = ".session";
    static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    readonly string path;

    HookSession(string path, string? head, HashSet<string> pushed, HashSet<string> reported)
    {
        this.path = path;
        Head = head;
        Pushed = pushed;
        Reported = reported;
    }

    public string? Head { get; set; }

    public HashSet<string> Pushed { get; }

    public HashSet<string> Reported { get; }

    public static string FolderOf(string docsFolder) => Path.Combine(docsFolder, FolderName);

    public static string PathOf(string docsFolder, string sessionId) =>
        Path.Combine(FolderOf(docsFolder), UnsafeCharacters().Replace(sessionId, "_") + ".json");

    /// <summary>Holds the session folder lock while <paramref name="change"/> reads and updates the session.</summary>
    public static T Update<T>(string docsFolder, string sessionId, Func<HookSession, T> change)
    {
        var folder = FolderOf(docsFolder);
        Directory.CreateDirectory(folder);
        var ignoreFile = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignoreFile))
            File.WriteAllText(ignoreFile, "*\n");

        using var lease = FolderLock.Acquire(folder, LockTimeout);
        var session = Load(PathOf(docsFolder, sessionId));
        var result = change(session);
        session.Save();
        return result;
    }

    public static HookSession? Read(string docsFolder, string sessionId)
    {
        var file = PathOf(docsFolder, sessionId);
        return File.Exists(file) ? Load(file) : null;
    }

    public static void DeleteOld(string docsFolder, DateTime utcNow)
    {
        var folder = FolderOf(docsFolder);
        if (!Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            if (utcNow - File.GetLastWriteTimeUtc(file) > MaxAge)
                File.Delete(file);
    }

    static HookSession Load(string file)
    {
        JsonObject? root = null;
        try
        {
            root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (JsonException)
        {
        }

        return new HookSession(file, root?["head"]?.GetValue<string>(), Strings(root?["pushed"]), Strings(root?["reported"]));
    }

    static HashSet<string> Strings(JsonNode? node) =>
        new((node as JsonArray ?? []).Select(item => item?.GetValue<string>()).OfType<string>(), StringComparer.Ordinal);

    void Save()
    {
        var root = new JsonObject
        {
            ["head"] = Head,
            ["pushed"] = new JsonArray([.. Pushed.Order(StringComparer.Ordinal).Select(key => (JsonNode)key)]),
            ["reported"] = new JsonArray([.. Reported.Order(StringComparer.Ordinal).Select(key => (JsonNode)key)]),
        };
        File.WriteAllText(path, root.ToJsonString(WriteOptions) + "\n");
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex UnsafeCharacters();
}
