using RepoInfraCli.Cli;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console.Testing;

namespace RepoInfraCli.Tests.Secrets;

public sealed class SecretsRepo : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("infra-keys-").FullName;

    public TempRepo Repo { get; } = new();

    public Dictionary<string, string> Variables { get; } = [];

    public string KeyFile => Path.Combine(home, ".config", "repo-infra", "key");

    public SecretEnvironment Machine => new(name => Variables.GetValueOrDefault(name), KeyFile);

    public string StoreText => Repo.ReadFile($"{RepoLocator.InfraFolderName}/{SecretStore.FileName}");

    public SecretStore Store => SecretStore.Load(Repo.Workspace);

    public RunResult Run(params string[] args) => RunWith(null, null, args);

    public RunResult RunWithInput(string stdin, params string[] args) => RunWith(stdin, null, args);

    public RunResult RunInteractive(string typed, params string[] args) => RunWith(null, typed, args);

    RunResult RunWith(string? stdin, string? typed, string[] args)
    {
        var output = new TestConsole().Width(250);
        var errors = new TestConsole().Width(250);
        if (typed is not null)
        {
            output.Interactive();
            output.Input.PushTextWithEnter(typed);
        }

        using var input = new StringReader(stdin ?? "");
        var exitCode = InfraApp.Create(output, errors, input, Machine).Run([.. args, "--root", Repo.Root]);
        return new RunResult(exitCode, output.Output, errors.Output);
    }

    public SecretKey LoadKey() => Machine.RequireKey();

    public void Dispose()
    {
        Repo.Dispose();
        Directory.Delete(home, recursive: true);
    }
}
