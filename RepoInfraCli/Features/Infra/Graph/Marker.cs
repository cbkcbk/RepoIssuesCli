namespace RepoInfraCli.Features.Infra.Graph;

public sealed record Marker(string Project, string? Env)
{
    public const string Prefix = "infra:";

    static readonly HashSet<string> NamePrefixKinds = ["firewall", "ssh_key", "registry"];

    public string Owner => Env is null ? Project : $"{Project}-{Env}";

    public string For(string name, bool protect = false, bool exported = false) =>
        $"{Prefix}{Owner} name={name}" + (protect ? " protect" : "") + (exported ? " exported" : "");

    public string For(ResourceNode resource) => For(resource.Name, resource.Protect, resource.Exported);

    public string PrefixedName(string name) => $"{Owner}-{name}";

    public static bool UsesNamePrefix(string kind) => NamePrefixKinds.Contains(kind);
}
