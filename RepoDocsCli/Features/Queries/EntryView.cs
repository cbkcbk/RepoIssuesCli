using System.Text.Json.Serialization;
using RepoDocsCli.Features.Docs;

namespace RepoDocsCli.Features.Queries;

public sealed record EntryView(
    string Address,
    string Feature,
    string Id,
    string Kind,
    string Summary,
    WhenHints? When,
    IReadOnlyList<PlanStep> Steps,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    DateOnly? Read,
    bool HasBody,
    string? Body,
    PlanProgress? Progress,
    EntryMatch? Match = null)
{
    public static EntryView Create(string owner, FeatureEntry entry, bool withBody, PlanLookup plans, EntryMatch? match = null)
    {
        var hasBody = !string.IsNullOrWhiteSpace(entry.Body);
        return new EntryView(
            EntryId.Address(owner, entry.Id),
            owner,
            entry.Id,
            entry.Kind,
            entry.Summary,
            entry.When,
            entry.Steps,
            entry.Created,
            entry.Updated,
            entry.Read,
            hasBody,
            hasBody && withBody ? entry.Body : null,
            plans.For(entry),
            match);
    }
}

public enum EntryMatch
{
    [JsonStringEnumMemberName("repo")]
    Repo,

    [JsonStringEnumMemberName("feature")]
    Feature,

    [JsonStringEnumMemberName("entry")]
    Entry,
}
