using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed record OwnerMark(string Project, string? Env, string Name, bool Protect = false, bool Exported = false)
{
    public const string EnvTagPrefix = "infra-env:";
    public const string ProtectTag = "infra-protect";
    public const string ExportedTag = "infra-exported";
    const string NamePrefix = "name=";

    public Marker Marker => new(Project, Env);

    public string Text => Marker.For(Name, Protect, Exported);

    public IReadOnlyList<string> Tags =>
    [
        Marker.Prefix + Project,
        .. Env is null ? Array.Empty<string>() : [EnvTagPrefix + Env],
        .. Protect ? [ProtectTag] : Array.Empty<string>(),
        .. Exported ? [ExportedTag] : Array.Empty<string>()
    ];

    public bool Keeps => Protect || Exported;

    public string KeepReason => Protect ? "protected" : "exported";

    public bool BelongsTo(Marker marker) => Project == marker.Project && Env == marker.Env;

    public static bool IsMarkerTag(string tag) =>
        tag.StartsWith(Marker.Prefix, StringComparison.Ordinal) || tag.StartsWith(EnvTagPrefix, StringComparison.Ordinal) || tag is ProtectTag or ExportedTag;

    public static OwnerMark For(ResourceNode node, Marker marker) => new(marker.Project, marker.Env, node.Name, node.Protect, node.Exported);

    public static OwnerMark? Parse(string? text)
    {
        var parts = new Queue<string>((text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!parts.TryDequeue(out var project) || !project.StartsWith(Marker.Prefix, StringComparison.Ordinal))
            return null;
        var env = parts.TryPeek(out var next) && next.StartsWith(Marker.EnvPrefix, StringComparison.Ordinal) ? parts.Dequeue()[Marker.EnvPrefix.Length..] : null;
        if (!parts.TryDequeue(out var name) || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
            return null;
        var flags = parts.ToHashSet(StringComparer.Ordinal);
        return new OwnerMark(project[Marker.Prefix.Length..], env, name[NamePrefix.Length..], flags.Contains("protect"), flags.Contains("exported"));
    }

    public static OwnerMark? FromTags(IReadOnlyCollection<string> tags, string name)
    {
        var project = tags.FirstOrDefault(tag => tag.StartsWith(Marker.Prefix, StringComparison.Ordinal));
        var env = tags.FirstOrDefault(tag => tag.StartsWith(EnvTagPrefix, StringComparison.Ordinal));
        return project is null
            ? null
            : new OwnerMark(project[Marker.Prefix.Length..], env?[EnvTagPrefix.Length..], name, tags.Contains(ProtectTag), tags.Contains(ExportedTag));
    }

    public static OwnerMark? FromPrefixedName(string liveName, Marker marker)
    {
        var parts = liveName.Split(Marker.NameSeparator);
        var expected = marker.Env is null ? [marker.Project] : (string[])[marker.Project, marker.Env];
        return parts.Length == expected.Length + 1 && parts.Take(expected.Length).SequenceEqual(expected, StringComparer.Ordinal) && parts[^1].Length > 0
            ? new OwnerMark(marker.Project, marker.Env, parts[^1])
            : null;
    }
}