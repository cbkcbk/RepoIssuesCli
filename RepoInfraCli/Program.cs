using RepoInfraCli.Features.Infra;

var workspace = InfraWorkspace.Open(RootArgument(args));

if (!workspace.IsInitialized)
{
    Console.Error.WriteLine($"No {RepoLocator.InfraFolderName}/{InfraWorkspace.EntryFileName} in {workspace.Root}.");
    return 1;
}

Directory.SetCurrentDirectory(workspace.Root);
Console.WriteLine($"Running {Path.GetRelativePath(workspace.Root, workspace.EntryFile)} in {workspace.Root}");
return 0;

static string? RootArgument(string[] args)
{
    var index = Array.IndexOf(args, "--root");
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}