using System.Text.Json.Nodes;
using RepoDocsCli.Features.Affected;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Queries;

namespace RepoDocsCli.Features.Hooks;

/// <summary>Turns a Claude Code hook's input into its JSON output; <c>null</c> means print nothing.</summary>
public static class DocsHooks
{
    public const int MaxAffected = 10;
    const string DocsFileNudgeKey = "docs-files";
    static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

    public static JsonObject? Handle(HookEvent hookEvent, HookInput input, string? explicitRoot)
    {
        var root = RepoLocator.FindRoot(explicitRoot, input.Cwd ?? Environment.CurrentDirectory);
        var store = new DocsStore(root) { LockTimeout = LockTimeout };
        if (!store.IsInitialized)
            return null;

        return hookEvent switch
        {
            HookEvent.SessionStart => Start(store, input),
            HookEvent.Read or HookEvent.Edit => Push(store, input, hookEvent),
            HookEvent.Stop => Stop(store, input),
            _ => null,
        };
    }

    static JsonObject? Start(DocsStore store, HookInput input)
    {
        if (input.SessionId is not { } sessionId)
            return null;

        var head = Git.Run(store.Root, "rev-parse", "--verify", "-q", "HEAD") is { Succeeded: true } result ? result.Output.Trim() : null;
        HookSession.Update(store.Folder, sessionId, session => session.Head ??= head);
        HookSession.DeleteOld(store.Folder, DateTime.UtcNow);
        return null;
    }

    static JsonObject? Push(DocsStore store, HookInput input, HookEvent hookEvent)
    {
        if (input.FilePath is not { } filePath || RelativePath(store.Root, filePath, input.Cwd) is not { } path)
            return null;

        if (path.StartsWith(RepoLocator.DocsFolderName + "/", StringComparison.OrdinalIgnoreCase))
            return Once(store, input, DocsFileNudgeKey) ? ToolContext(DocsFileNudge) : null;

        var entries = DocsPush.For(store.Load(), path, hookEvent, store.Root);
        var unpushed = input.SessionId is { } sessionId
            ? HookSession.Update(store.Folder, sessionId, session => Claim(session, entries))
            : entries;
        if (unpushed.Count == 0)
            return null;

        var shown = unpushed.Take(DocsPush.MaxEntries).ToList();
        MarkRead(store, shown.Where(entry => entry.Full));
        return ToolContext(DocsPush.Context(path, shown, unpushed.Count - shown.Count));
    }

    static List<PushedEntry> Claim(HookSession session, IReadOnlyList<PushedEntry> entries)
    {
        var unpushed = entries.Where(entry => !session.Pushed.Contains(entry.Key)).ToList();
        foreach (var entry in unpushed.Take(DocsPush.MaxEntries))
            session.Pushed.Add(entry.Key);
        return unpushed;
    }

    static JsonObject? Stop(DocsStore store, HookInput input)
    {
        if (input.StopHookActive)
            return null;

        var since = (input.SessionId is { } id ? HookSession.Read(store.Folder, id)?.Head : null) ?? GitDiff.WorkingHead;
        var affected = DocsAffected.Find(store.Root, store.Load(), since).Entries;
        var fresh = input.SessionId is { } sessionId
            ? HookSession.Update(store.Folder, sessionId, session => affected.Where(entry => session.Reported.Add(entry.Address)).ToList())
            : [.. affected];
        if (fresh.Count == 0)
            return null;

        return new JsonObject { ["decision"] = "block", ["reason"] = StopReason(fresh, since) };
    }

    static string StopReason(IReadOnlyList<AffectedEntry> entries, string since)
    {
        var lines = new List<string> { $"These docs cover code changed in this session but were not updated (docs affected --since {Short(since)}):" };
        lines.AddRange(entries.Take(MaxAffected).Select(entry => $"- {entry.Address} ({entry.Kind}){(entry.Summary is { } summary ? ": " + summary : "")}"));
        if (entries.Count > MaxAffected)
            lines.Add($"- and {entries.Count - MaxAffected} more: run `docs affected --since {since} --json`.");
        lines.Add("Review each one against the code. Fix it with `docs edit <address>` (re-anchor changed code with `--add-anchor path:line`), "
                  + "or run `docs touch <address>` when it is still correct. Use the docs CLI only; never edit .repo-docs/ files directly. "
                  + "If you can't settle one, tell the user.");
        return string.Join('\n', lines);
    }

    static string Short(string since) => since.Length == 40 && since.All(char.IsAsciiHexDigit) ? since[..12] : since;

    const string DocsFileNudge =
        "Never read or edit .repo-docs/ files directly. Read docs with `docs show`, `docs for` or `docs find`, and change them with `docs add`, `docs edit`, `docs rm` or `docs touch`. "
        + "The only exception is resolving a git merge conflict, followed by `docs check`.";

    static bool Once(DocsStore store, HookInput input, string key) =>
        input.SessionId is not { } sessionId || HookSession.Update(store.Folder, sessionId, session => session.Pushed.Add(key));

    static void MarkRead(DocsStore store, IEnumerable<PushedEntry> entries)
    {
        try
        {
            using var lease = store.Lock();
            foreach (var entry in entries)
                store.MarkRead(entry.View.Feature, entry.View.Id);
        }
        catch (LockTimeoutException)
        {
        }
    }

    static string? RelativePath(string root, string filePath, string? cwd)
    {
        try
        {
            var path = DocsFor.RepoRelative(root, Path.GetFullPath(filePath, cwd ?? root), root);
            return path.Length == 0 ? null : path;
        }
        catch (UsageException)
        {
            return null;
        }
    }

    static JsonObject ToolContext(string text) => new()
    {
        ["hookSpecificOutput"] = new JsonObject
        {
            ["hookEventName"] = "PostToolUse",
            ["additionalContext"] = text,
        },
    };
}
