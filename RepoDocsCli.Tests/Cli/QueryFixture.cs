namespace RepoDocsCli.Tests.Cli;

public static class QueryFixture
{
    public const string Repo =
        """
        summary: Monorepo for the shop
        entries:
          - id: style
            kind: rule
            summary: Use file-scoped namespaces
          - id: history
            kind: decision
            summary: Chose YAML over JSON
        """;

    public const string Billing =
        """
        feature: billing
        status: in-progress
        summary: Invoices and payments
        when:
          paths:
            - App/Features/Billing/
          keywords:
            - invoice
        entries:
          - id: rounding
            kind: rule
            summary: Round half to even
            body: |
              Use MidpointRounding.ToEven.
          - id: money
            kind: gotcha
            summary: Money is a decimal
            when:
              paths:
                - App/Shared/Money.cs
          - id: rollout
            kind: plan
            summary: Roll out invoices
            steps:
              - title: Schema
                issues: [1, 2]
          - id: why-yaml
            kind: decision
            summary: Invoices are stored as YAML
        """;

    public const string Search =
        """
        feature: search
        status: shipped
        summary: Full-text search
        when:
          anchors:
            - path: App/Search/Index.cs
              line: 3
              span: 1
              context: aaaaaaaaaaaa
              body: bbbbbbbbbbbb
        entries:
          - id: ranking
            kind: fact
            summary: Ranking uses BM25
          - id: launch
            kind: plan
            summary: Launch search
        """;

    public static void Write(TempRepo repo)
    {
        repo.Store.Initialize();
        repo.WriteDocsFile("repo.yaml", Repo + "\n");
        repo.WriteDocsFile("billing.yaml", Billing + "\n");
        repo.WriteDocsFile("search.yaml", Search + "\n");
    }

    public static void WriteIssues(TempRepo repo)
    {
        var folder = repo.Subfolder(".repo-issues");
        File.WriteAllText(Path.Combine(folder, "00001-schema.json"), """{ "number": 1, "name": "Schema", "status": "Done" }""");
        File.WriteAllText(Path.Combine(folder, "00002-store.json"), """{ "number": 2, "name": "Store", "status": "InProgress" }""");
    }
}
