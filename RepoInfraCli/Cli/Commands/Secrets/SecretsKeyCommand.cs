using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands.Secrets;

[Description("Print this machine's public key so a recipient can run 'infra secrets recipients add <name> <key>' for you.")]
public sealed class SecretsKeyCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine)
    : SecretsCommand<InfraSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, InfraSettings settings, InfraLog log)
    {
        using var key = Machine.LoadKey() ?? throw new SecretNotFoundException(
            $"No repo-infra key on this machine ({Machine.KeyFile}) and {SecretEnvironment.KeyVariable} is not set; run 'infra secrets init'.");
        var recipient = SecretStore.Exists(workspace) ? SecretStore.Load(workspace).RecipientWith(key.PublicKey) : null;
        var result = new SecretsKeyResult(key.PublicKey, Machine.KeySource, recipient);

        if (settings.Json)
            Json(result);
        else
        {
            Console.Profile.Out.Writer.Write(key.PublicKey);
            Console.Profile.Out.Writer.Write('\n');
        }
        return InfraExitCodes.Success;
    }
}
