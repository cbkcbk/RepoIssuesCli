using RepoIssuesCli.Cli;
using RepoIssuesCli.Features.Issues;
using Spectre.Console.Testing;

namespace RepoIssuesCli.Tests;

public sealed record RunResult(int ExitCode, string Output, string Errors);

public sealed class TempRepo : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("ri-tests-").FullName;

    public IssueStore Store => new(Root);

    public IReadOnlyList<Issue> Issues => Store.Load().Issues;

    public RunResult Run(params string[] args) => Run(new TestConsole().Width(250), args);

    public RunResult Run(TestConsole output, params string[] args)
    {
        var errors = new TestConsole().Width(250);
        var exitCode = RiApp.Create(output, errors).Run([.. args, "--root", Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    public Issue Add(string name, params string[] args)
    {
        var result = Run(["new", name, "--json", .. args]);
        Assert.That(result.ExitCode, Is.Zero, result.Errors);
        return IssueJson.Deserialize<Issue>(result.Output)!;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
