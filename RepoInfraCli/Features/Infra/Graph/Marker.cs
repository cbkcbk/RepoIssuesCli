namespace RepoInfraCli.Features.Infra.Graph;

public sealed record Marker(string Project, string? Env)
{
    public const string Prefix = "infra:";
    public const string EnvPrefix = "env=";
    public const string NameSeparator = "--";

    static readonly HashSet<string> NamePrefixKinds = ["firewall", "ssh_key", "registry"];

    public string Owner => Env is null ? Project : $"{Project}-{Env}";

    public string Label => $"{Prefix}{Project}" + (Env is null ? "" : $" {EnvPrefix}{Env}");

    public string For(string name, bool protect = false, bool exported = false) =>
        $"{Label} name={name}" + (protect ? " protect" : "") + (exported ? " exported" : "");

    public string For(ResourceNode resource) => For(resource.Name, resource.Protect, resource.Exported);

    public string PrefixedName(string name) => string.Join(NameSeparator, Env is null ? [Project, name] : [Project, Env, name]);

    public static bool UsesNamePrefix(string kind) => NamePrefixKinds.Contains(kind);
}