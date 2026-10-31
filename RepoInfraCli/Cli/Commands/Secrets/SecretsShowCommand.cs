using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands.Secrets;

public sealed class SecretsShowSettings : InfraSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Exact secret name as stored (prod/db/password or db/password).")]
    public string Name { get; init; } = "";
}

[Description("Decrypt one secret and print its value to stdout. This is the only command that reveals values.")]
public sealed class SecretsShowCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine)
    : SecretsCommand<SecretsShowSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, SecretsShowSettings settings, InfraLog log)
    {
        RejectEnv(settings);
        var store = RequireStore(workspace);
        using var key = Machine.LoadKey();
        var reveal = store.Reveal(settings.Name, key);
        switch (reveal.Status)
        {
            case SecretRevealStatus.Missing:
                throw new SecretNotFoundException($"No secret named '{settings.Name}'; run '{SecretProblems.SetCommand(null, settings.Name)}'.");
            case SecretRevealStatus.NoKey:
                throw new UsageException($"No repo-infra key on this machine ({Machine.KeyFile}) and {SecretEnvironment.KeyVariable} is not set; run 'infra secrets init'.");
            case SecretRevealStatus.NotRecipient:
                log.Error("secrets.not-recipient", $"This machine's key cannot decrypt '{settings.Name}'; have a recipient run 'infra secrets recipients add <name> <key>' with the output of 'infra secrets key'.");
                return InfraExitCodes.Invalid;
            case SecretRevealStatus.Tampered:
                log.Error("secrets.tampered", $"'{settings.Name}' failed authentication: the stored value was modified, copied from another name or is corrupt.");
                return InfraExitCodes.Invalid;
        }

        if (settings.Json)
            Json(new SecretShowResult(settings.Name, reveal.Value!));
        else
        {
            Console.Profile.Out.Writer.Write(reveal.Value);
            Console.Profile.Out.Writer.Write('\n');
        }
        return InfraExitCodes.Success;
    }
}
