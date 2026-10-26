namespace RepoDocsCli.Features.Docs;

public sealed record ConflictSides(string Ours, string Theirs, string? Ancestor);

public static class ConflictMarkers
{
    const string OursMarker = "<<<<<<<";
    const string AncestorMarker = "|||||||";
    const string SeparatorMarker = "=======";
    const string TheirsMarker = ">>>>>>>";

    enum Section { Common, Ours, Ancestor, Theirs }

    public static bool Contains(string text) => text.Split('\n').Any(line => IsMarker(line, OursMarker));

    public static ConflictSides? Split(string text)
    {
        var ours = new List<string>();
        var theirs = new List<string>();
        var ancestor = new List<string>();
        var section = Section.Common;
        var hunks = 0;
        var hunksWithAncestor = 0;

        foreach (var line in text.Split('\n'))
        {
            var next = (section, Marker(line)) switch
            {
                (Section.Common, OursMarker) => Section.Ours,
                (Section.Ours, AncestorMarker) => Section.Ancestor,
                (Section.Ours or Section.Ancestor, SeparatorMarker) => Section.Theirs,
                (Section.Theirs, TheirsMarker) => Section.Common,
                (_, null) => section,
                _ => (Section?)null,
            };
            if (next is not { } nextSection)
                return null;

            if (nextSection != section)
            {
                if (nextSection == Section.Ours)
                    hunks++;
                if (nextSection == Section.Ancestor)
                    hunksWithAncestor++;
                section = nextSection;
                continue;
            }

            if (section is Section.Common or Section.Ours)
                ours.Add(line);
            if (section is Section.Common or Section.Theirs)
                theirs.Add(line);
            if (section is Section.Common or Section.Ancestor)
                ancestor.Add(line);
        }

        if (section != Section.Common || hunks == 0)
            return null;

        return new ConflictSides(Join(ours), Join(theirs), hunksWithAncestor == hunks ? Join(ancestor) : null);
    }

    static string Join(List<string> lines) => string.Join('\n', lines);

    static string? Marker(string line) =>
        new[] { OursMarker, AncestorMarker, SeparatorMarker, TheirsMarker }.FirstOrDefault(marker => IsMarker(line, marker));

    static bool IsMarker(string line, string marker)
    {
        var trimmed = line.TrimEnd('\r');
        return trimmed == marker || (marker != SeparatorMarker && trimmed.StartsWith(marker + " ", StringComparison.Ordinal));
    }
}
