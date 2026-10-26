namespace RepoDocsCli.Features.Docs;

public sealed class DocsChangeRejectedException(IReadOnlyList<ValidationProblem> problems)
    : UsageException("The change would make the docs invalid:" + string.Concat(problems.Select(problem => $"\n  {problem.Message}")))
{
    public IReadOnlyList<ValidationProblem> Problems => problems;
}

public static class DocsChange
{
    public static DocsSnapshot WithFeature(DocsSnapshot snapshot, string? replacing, Feature? feature)
    {
        var features = snapshot.Features.Where(existing => existing.Id != replacing && existing.Id != feature?.Id).ToList();
        if (feature is not null)
            features.Add(feature);
        return snapshot with { Features = features };
    }

    public static void EnsureValid(DocsSnapshot before, DocsSnapshot after)
    {
        var existing = DocsValidator.Validate(before).ToHashSet();
        var introduced = DocsValidator.Validate(after).Where(problem => !existing.Contains(problem)).ToList();
        if (introduced.Count > 0)
            throw new DocsChangeRejectedException(introduced);
    }
}
