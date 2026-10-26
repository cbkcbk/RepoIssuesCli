using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public sealed record SearchHit(string Address, string Feature, string Kind, string? Summary, int Score);

public static class DocsSearch
{
    public const string FeatureKind = "feature";
    public const int IdWeight = 1000;
    public const int SummaryWeight = 100;
    public const int KeywordWeight = 10;
    public const int BodyWeight = 1;

    const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    public static IReadOnlyList<SearchHit> Find(DocsSnapshot snapshot, string query, int limit)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0)
            return [];

        var exact = query.Trim();
        var hits = new List<SearchHit>();

        foreach (var entry in snapshot.Repo.Entries)
            hits.Add(EntryHit(FeatureId.RepoFileStem, entry, exact, terms));

        foreach (var feature in snapshot.Features)
        {
            var score = Score([feature.Id], exact, terms, feature.Summary, feature.When.Keywords, null);
            hits.Add(new SearchHit(feature.Id, feature.Id, FeatureKind, feature.Summary, score));
            hits.AddRange(feature.Entries.Select(entry => EntryHit(feature.Id, entry, exact, terms)));
        }

        return hits
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Address, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    static SearchHit EntryHit(string owner, FeatureEntry entry, string exact, IReadOnlyList<string> terms)
    {
        var address = EntryId.Address(owner, entry.Id);
        var score = Score([entry.Id, address], exact, terms, entry.Summary, entry.When?.Keywords ?? [], entry.Body);
        return new SearchHit(address, owner, entry.Kind, entry.Summary, score);
    }

    static int Score(string[] ids, string exact, IReadOnlyList<string> terms, string? summary, IReadOnlyList<string> keywords, string? body)
    {
        var idHit = ids.Any(id => id.Equals(exact, Comparison) || terms.Any(term => id.Equals(term, Comparison)));
        return (idHit ? IdWeight : 0)
               + SummaryWeight * terms.Count(term => summary?.Contains(term, Comparison) == true)
               + KeywordWeight * terms.Count(term => keywords.Any(keyword => keyword.Contains(term, Comparison)))
               + BodyWeight * terms.Count(term => body?.Contains(term, Comparison) == true);
    }
}
