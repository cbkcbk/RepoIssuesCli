using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands.Secrets;

public sealed class RecipientsRemoveSettings : InfraSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Recipient name as listed by 'infra secrets list'.")]
    public string Name { get; init; } = "";
}

[Description("Remove a recipient: secrets this machine can decrypt are re-encrypted under a fresh data key without them, others just lose their wrap. Lists every secret they could read; rotate those.")]
public sealed class RecipientsRemoveCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine)
    : SecretsCommand<RecipientsRemoveSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, RecipientsRemoveSettings settings, InfraLog log)
    {
        RejectEnv(settings);
        var store = RequireStore(workspace);
        using var key = Machine.LoadKey();
        var change = store.RemoveRecipient(settings.Name, key);
        store.Save(workspace);
        var result = new RecipientRemovedResult(change.Recipient, change.Secrets, change.Skipped);

        if (settings.Json)
        {
            Json(result);
            return InfraExitCodes.Success;
        }

        Line($"Removed recipient [bold]{Markup.Escape(change.Recipient)}[/]");
        if (change.Secrets.Count == 0)
            return InfraExitCodes.Success;

        Line($"[yellow]Rotate these {change.Secrets.Count} secret(s); {Markup.Escape(change.Recipient)} could read them:[/]");
        foreach (var secret in change.Secrets)
            Line($"  {Markup.Escape(secret)}  [grey]infra secrets set {Markup.Escape(secret)}[/]");
        if (change.Skipped.Count > 0)
            Line($"[grey]Not re-encrypted (this machine cannot decrypt them): {Markup.Escape(string.Join(", ", change.Skipped))}[/]");
        return InfraExitCodes.Success;
    }
}
