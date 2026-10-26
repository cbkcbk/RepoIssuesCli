namespace RepoCli.Core.Features.Anchors;

/// <summary>Past versions of a file, newest first: the index, then recent commits. Loaded one at a time.</summary>
sealed class AnchorHistory(string root, string path)
{
    public const int Depth = 20;

    readonly List<AnchorFile?> loaded = [];
    List<string>? revisions;

    public IEnumerable<AnchorFile> Versions()
    {
        revisions ??= ["", .. AnchorGit.Commits(root, path, Depth)];
        for (var index = 0; index < revisions.Count; index++)
        {
            if (index == loaded.Count)
                loaded.Add(AnchorGit.Show(root, revisions[index], path) is { } content ? new AnchorFile(path, SourceText.Parse(content)) : null);
            if (loaded[index] is { } version)
                yield return version;
        }
    }
}
