namespace RepoCli.Core.Features.IssueLinks;

public sealed record DocsReference(string Address, string StepTitle, int IssueNumber, string File)
{
    public string Describe() => StepTitle.Length == 0 ? Address : $"{Address} ({StepTitle})";
}

public sealed record DocsReferenceError(string File, string Message);

public sealed record DocsReferenceScan(IReadOnlyList<DocsReference> References, IReadOnlyList<DocsReferenceError> Errors)
{
    public static DocsReferenceScan Empty { get; } = new([], []);

    public DocsReferenceScan To(int number) => this with { References = References.Where(reference => reference.IssueNumber == number).ToList() };
}
