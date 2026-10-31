using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands.Secrets;

public sealed class RecipientsAddSettings : InfraSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Recipient name, e.g. a person (\"Ada <ada@example.com>\") or a pipeline (ci-github).")]
    public string Name { get; init; } = "";

    [CommandArgument(1, "<PUBKEY>")]
    [Description("Their public key from 'infra secrets key' (p256:...).")]
    public string PublicKey { get; init; } = "";
}

[Description("Add a recipient and re-wrap every secret this machine can decrypt for them. Rerun to share secrets added before they joined.")]
public sealed class RecipientsAddCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine)
    : SecretsCommand<RecipientsAddSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, RecipientsAddSettings settings, InfraLog log)
    {
        RejectEnv(settings);
        var name = settings.Name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
            throw new UsageException("Recipient name must be non-empty text on one line.");

        string publicKey;
        try
        {
            publicKey = RecipientKey.Normalize(settings.PublicKey);
        }
        catch (FormatException exception)
        {
            throw new UsageException(exception.Message);
        }

        var store = RequireStore(workspace);
        using var key = store.Secrets.Count == 0 ? Machine.LoadKey() : Machine.RequireKey();
        var change = store.AddRecipient(name, publicKey, key);
        store.Save(workspace);
        var result = new RecipientAddedResult(name, publicKey, change.Secrets, change.Skipped);

        if (settings.Json)
        {
            Json(result);
            return InfraExitCodes.Success;
        }

        Line($"Recipient [bold]{Markup.Escape(name)}[/] can now read {change.Secrets.Count} newly shared secret(s)");
        if (change.Skipped.Count > 0)
            Line($"[yellow]Not shared (this machine cannot decrypt them):[/] {Markup.Escape(string.Join(", ", change.Skipped))}");
        return InfraExitCodes.Success;
    }
}
