using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed record OwnerMark(string Owner, string Name, bool Protect = false, bool Exported = false)
{
    public const string ProtectTag = "infra-protect";
    public const string ExportedTag = "infra-exported";
    const string NamePrefix = "name=";

    public string Text => new Marker(Owner, null).For(Name, Protect, Exported);

    public IReadOnlyList<string> Tags =>
        [OwnerTag(Owner), .. Protect ? [ProtectTag] : Array.Empty<string>(), .. Exported ? [ExportedTag] : Array.Empty<string>()];

    public bool Keeps => Protect || Exported;

    public string KeepReason => Protect ? "protected" : "exported";

    public static string OwnerTag(string owner) => Marker.Prefix + owner;

    public static bool IsMarkerTag(string tag) => tag.StartsWith(Marker.Prefix, StringComparison.Ordinal) || tag is ProtectTag or ExportedTag;

    public static OwnerMark For(ResourceNode node, Marker marker) => new(marker.Owner, node.Name, node.Protect, node.Exported);

    public static OwnerMark? Parse(string? text)
    {
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !parts[0].StartsWith(Marker.Prefix, StringComparison.Ordinal) || !parts[1].StartsWith(NamePrefix, StringComparison.Ordinal))
            return null;
        var flags = parts.Skip(2).ToHashSet(StringComparer.Ordinal);
        return new OwnerMark(parts[0][Marker.Prefix.Length..], parts[1][NamePrefix.Length..], flags.Contains("protect"), flags.Contains("exported"));
    }

    public static OwnerMark? FromTags(IReadOnlyCollection<string> tags, string name)
    {
        var owner = tags.FirstOrDefault(tag => tag.StartsWith(Marker.Prefix, StringComparison.Ordinal));
        return owner is null ? null : new OwnerMark(owner[Marker.Prefix.Length..], name, tags.Contains(ProtectTag), tags.Contains(ExportedTag));
    }

    public static OwnerMark? FromPrefixedName(string liveName, Marker marker)
    {
        var prefix = marker.PrefixedName("");
        return liveName.Length > prefix.Length && liveName.StartsWith(prefix, StringComparison.Ordinal)
            ? new OwnerMark(marker.Owner, liveName[prefix.Length..])
            : null;
    }
}
