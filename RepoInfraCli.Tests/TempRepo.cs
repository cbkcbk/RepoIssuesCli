using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Evaluation;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests;

public sealed record RunResult(int ExitCode, string Output, string Errors);

public sealed class TempRepo : IDisposable
{
    public const string Header = """
        local infra = require("@infra")
        infra.project "test" {}

        """;

    public string Root { get; } = Directory.CreateTempSubdirectory("infra-tests-").FullName;

    public InfraWorkspace Workspace => new(Root);

    public string Folder => Workspace.Folder;

    public RunResult Run(params string[] args) => RunWithoutRoot([.. args, "--root", Root]);

    public static RunResult RunWithoutRoot(params string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        var exitCode = InfraApp.Create(output, errors).Run(args);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    public void WriteInfra(string script) => WriteFile(Path.Combine(RepoLocator.InfraFolderName, InfraWorkspace.EntryFileName), script);

    public void WriteScript(string body) => WriteInfra(Header + body);

    public string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.ReplaceLineEndings("\n"));
        return full;
    }

    public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public void CopyDemo()
    {
        Directory.CreateDirectory(Folder);
        File.Copy(DemoEntry, Workspace.EntryFile, overwrite: true);
    }

    public EvaluationResult Evaluate(string? env = null, EvaluationMode mode = EvaluationMode.Full) =>
        InfraEvaluator.Evaluate(Workspace, new EvaluationOptions { Env = env, Mode = mode });

    public static string DemoEntry { get; } = FindDemo();

    static string FindDemo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "RepoInfraCli", "demo", RepoLocator.InfraFolderName, InfraWorkspace.EntryFileName);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("RepoInfraCli/demo/.repo-infra/infra.luau not found above the test output folder.");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, recursive: true);
    }
}
