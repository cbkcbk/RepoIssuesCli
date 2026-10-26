namespace RepoDocsCli.Features.Docs;

public static class EntryId
{
    public const char Separator = '/';

    public static bool IsValid(string? id) => FeatureId.IsKebabCase(id);

    public static string Address(string feature, string entry) => $"{feature}{Separator}{entry}";
}
