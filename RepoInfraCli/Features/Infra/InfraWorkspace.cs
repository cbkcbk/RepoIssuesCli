namespace RepoInfraCli.Features.Infra;

public sealed class InfraWorkspace(string root)
{
    public const string EntryFileName = "infra.luau";

    public string Root => root;

    public string Folder => Path.Combine(root, RepoLocator.InfraFolderName);

    public string EntryFile => Path.Combine(Folder, EntryFileName);

    public bool IsInitialized => File.Exists(EntryFile);

    public static InfraWorkspace Open(string? explicitRoot) =>
        new(RepoLocator.FindRoot(explicitRoot, Environment.CurrentDirectory));
}
