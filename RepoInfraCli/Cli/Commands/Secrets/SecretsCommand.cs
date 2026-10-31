using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands.Secrets;

public abstract class SecretsCommand<TSettings>(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine) : InfraCommand<TSettings>(console, errors)
    where TSettings : InfraSettings
{
    protected SecretEnvironment Machine => machine;

    protected static void RejectEnv(InfraSettings settings)
    {
        if (settings.Env is { } env)
            throw new UsageException($"Write the env into the secret name instead of --env (e.g. {env}/db/password).");
    }

    protected static SecretStore RequireStore(InfraWorkspace workspace) =>
        SecretStore.Exists(workspace)
            ? SecretStore.Load(workspace)
            : throw new UsageException($"No {RepoLocator.InfraFolderName}/{SecretStore.FileName} in {workspace.Root}; run 'infra secrets init' first.");

    protected void Line(string markup) => CliOutput.WriteUnwrappedLine(Console, markup);

    protected void Json<T>(T value) => CliOutput.WriteJson(Console, SecretsJson.Serialize(value));
}
