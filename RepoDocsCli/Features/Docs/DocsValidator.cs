namespace RepoDocsCli.Features.Docs;

public enum ProblemKind
{
    InvalidFile,
    UndeclaredKind,
    MissingSummary,
    MultiLineSummary,
    DuplicateEntryId,
    InvalidEntryId,
}

public sealed record ValidationProblem(ProblemKind Kind, string Message, string? File = null)
{
    public Severity Severity => Severity.Error;
}

public static class DocsValidator
{
    public static IReadOnlyList<ValidationProblem> Validate(DocsSnapshot snapshot)
    {
        var problems = new List<ValidationProblem>();

        problems.AddRange(snapshot.Errors.Select(error =>
            new ValidationProblem(ProblemKind.InvalidFile, error.Message, error.FileName)));

        problems.AddRange(ValidateEntries(FeatureId.RepoFileStem, snapshot.Repo.Entries, snapshot.Config));

        foreach (var feature in snapshot.Features)
        {
            if (IsMultiLine(feature.Summary))
                problems.Add(new ValidationProblem(ProblemKind.MultiLineSummary, $"Feature '{feature.Id}' has a summary over several lines.", DocsStore.FileNameFor(feature.Id)));

            problems.AddRange(ValidateEntries(feature.Id, feature.Entries, snapshot.Config));
        }

        return problems;
    }

    static IEnumerable<ValidationProblem> ValidateEntries(string owner, IReadOnlyList<FeatureEntry> entries, DocsConfig config)
    {
        var file = DocsStore.FileNameFor(owner);

        foreach (var entry in entries)
        {
            var name = EntryId.Address(owner, entry.Id ?? "?");

            if (!EntryId.IsValid(entry.Id))
                yield return new ValidationProblem(ProblemKind.InvalidEntryId,
                    entry.Id is null ? $"An entry in '{owner}' has no id." : $"Entry '{name}' has an invalid id; use lowercase letters, digits and single hyphens.", file);

            if (!config.Declares(entry.Kind))
                yield return new ValidationProblem(ProblemKind.UndeclaredKind,
                    entry.Kind is null ? $"Entry '{name}' has no kind." : $"Entry '{name}' has kind '{entry.Kind}', which config.yaml does not declare.", file);

            if (string.IsNullOrWhiteSpace(entry.Summary))
                yield return new ValidationProblem(ProblemKind.MissingSummary, $"Entry '{name}' has no summary.", file);
            else if (IsMultiLine(entry.Summary))
                yield return new ValidationProblem(ProblemKind.MultiLineSummary, $"Entry '{name}' has a summary over several lines; keep it to one line and move the rest to body.", file);
        }

        foreach (var duplicate in entries.Where(entry => entry.Id is not null).GroupBy(entry => entry.Id).Where(group => group.Count() > 1))
            yield return new ValidationProblem(ProblemKind.DuplicateEntryId, $"Entry id '{EntryId.Address(owner, duplicate.Key)}' is used {duplicate.Count()} times.", file);
    }

    static bool IsMultiLine(string? text) => text is not null && text.Trim().AsSpan().ContainsAny('\n', '\r');
}
