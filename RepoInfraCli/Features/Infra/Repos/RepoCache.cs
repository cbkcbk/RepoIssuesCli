using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RepoInfraCli.Features.Infra.Apps;

namespace RepoInfraCli.Features.Infra.Repos;

public sealed record RepoSource(string Url, string Ref, string? Path)
{
    public string Folder => (Path ?? RepoLocator.InfraFolderName).Replace('\\', '/').Trim('/');

    public string Display => $"{Url}@{Ref}";

    public static string Key(string url)
    {
        var trimmed = url.Trim().TrimEnd('/').ToLowerInvariant();
        return trimmed.EndsWith(".git", StringComparison.Ordinal) ? trimmed[..^4] : trimmed;
    }
}

public sealed record RepoCheckout(string Root, string Folder, string Commit, bool Offline)
{
    public string InfraFolder => System.IO.Path.Combine(Root, Folder);
}

public sealed class RepoFetchException(string message) : Exception(message);

public sealed partial class RepoCache(string root, IProcessRunner? runner = null)
{
    public const string RootVariable = "INFRA_REPO_CACHE";

    static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    readonly IProcessRunner git = runner ?? ProcessRunner.Instance;

    public static RepoCache Default => new(DefaultRoot());

    public static RepoCache DefaultOffline => new(DefaultRoot()) { Offline = true };

    /// <summary>Never clone or fetch: use the commit last pinned in the cache.</summary>
    public bool Offline { get; init; }

    public string Root => root;

    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "repo-infra", "repos");

    public string Folder(string url) => Path.Combine(root, Hash(RepoSource.Key(url)));

    public RepoCheckout Fetch(RepoSource source)
    {
        var folder = Folder(source.Url);
        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            if (Offline)
                throw new RepoFetchException($"there is no cached copy in {folder} and offline runs don't clone");
            Clone(source, folder);
        }

        Run(folder, $"set the sparse checkout of {source.Display}", "sparse-checkout", "set", "--no-cone", $"/{source.Folder}/");
        var (commit, offline) = Resolve(source, folder);
        Run(folder, $"check out {source.Display}", "-c", "advice.detachedHead=false", "checkout", "--quiet", "--force", "--detach", commit);
        return new RepoCheckout(folder, source.Folder, commit, offline);
    }

    public void Materialize(RepoCheckout checkout, string relativePath) =>
        Run(checkout.Root, $"fetch {relativePath}", "sparse-checkout", "add", "/" + relativePath.Replace('\\', '/').TrimStart('/'));

    void Clone(RepoSource source, string folder)
    {
        Directory.CreateDirectory(root);
        var partial = $"{folder}.partial-{Guid.NewGuid():N}";
        var result = Git(null, "clone", "--quiet", "--config", "core.autocrlf=false", "--filter=blob:none", "--sparse", "--no-checkout", source.Url, partial);
        if (!result.Succeeded)
        {
            Delete(partial);
            throw new RepoFetchException($"git clone {source.Url} failed and there is no cached copy in {folder}: {result.Tail(600)}");
        }

        Delete(folder);
        Directory.Move(partial, folder);
    }

    (string Commit, bool Offline) Resolve(RepoSource source, string folder)
    {
        if (IsSha(source.Ref) && Commit(folder, source.Ref) is { } known)
            return (known, false);

        var pinned = $"refs/repo-infra/{Hash(source.Ref)}";
        if (Offline)
            return Commit(folder, pinned) is { } last
                ? (last, true)
                : throw new RepoFetchException($"{source.Ref} was never fetched into the cache {folder} and offline runs don't fetch");

        var fetched = Git(folder, "fetch", "--quiet", "--filter=blob:none", "origin", source.Ref);
        if (fetched.Succeeded && Commit(folder, "FETCH_HEAD") is { } commit)
        {
            Run(folder, $"remember {source.Display}", "update-ref", pinned, commit);
            return (commit, false);
        }

        return Commit(folder, pinned) is { } cached
            ? (cached, true)
            : throw new RepoFetchException($"git fetch {source.Display} failed and {source.Ref} was never fetched into the cache {folder}: {fetched.Tail(600)}");
    }

    string? Commit(string folder, string revision) =>
        Git(folder, "rev-parse", "--verify", "--quiet", revision + "^{commit}") is { Succeeded: true } result ? result.Stdout.Trim() : null;

    void Run(string folder, string what, params string[] arguments)
    {
        var result = Git(folder, arguments);
        if (!result.Succeeded)
            throw new RepoFetchException($"git could not {what}: {result.Tail(600)}");
    }

    ProcessResult Git(string? folder, params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(GitTimeout);
        try
        {
            return git.RunAsync("git", folder is null ? arguments : ["-C", folder, .. arguments], stdin: "", cancellationToken: timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(-1, "", $"git {string.Join(' ', arguments)} timed out after {GitTimeout.TotalMinutes:0} minutes");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new ProcessResult(-1, "", $"git could not be started: {exception.Message}");
        }
    }

    static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    static bool IsSha(string text) => Sha().IsMatch(text);

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex Sha();
}