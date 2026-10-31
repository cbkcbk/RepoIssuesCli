namespace RepoInfraCli.Features.Infra;

public sealed class InfraWorkspace(string root)
{
    public const string EntryFileName = "infra.luau";
    public const string LuauRcFileName = ".luaurc";
    public const string StubsFolderName = ".luau";
    public const string EnvsFolderName = "envs";

    public string Root => root;

    public string Folder => Path.Combine(root, RepoLocator.InfraFolderName);

    public string EntryFile => Path.Combine(Folder, EntryFileName);

    public string LuauRcFile => Path.Combine(Folder, LuauRcFileName);

    public string StubsFolder => Path.Combine(Folder, StubsFolderName, "@infra");

    public string EnvsFolder => Path.Combine(Folder, EnvsFolderName);

    public bool IsInitialized => File.Exists(EntryFile);

    public string RelativePath(string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public static InfraWorkspace Open(string? explicitRoot) =>
        new(RepoLocator.FindRoot(explicitRoot, Environment.CurrentDirectory));

    public static bool IsInside(string folder, string fullPath)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
