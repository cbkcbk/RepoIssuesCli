using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands.Secrets;

public sealed class SecretsInitSettings : InfraSettings
{
    [CommandOption("--name <NAME>")]
    [Description("Recipient name for this machine. Defaults to your git user name and email.")]
    public string? Name { get; init; }
}

[Description("Create this machine's key (~/.config/repo-infra/key, or use REPO_INFRA_KEY) and .repo-infra/secrets.yaml, and add yourself as a recipient. Safe to rerun.")]
public sealed class SecretsInitCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine) : SecretsCommand<SecretsInitSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, SecretsInitSettings settings, InfraLog log)
    {
        RejectEnv(settings);
        var fileCreated = !SecretStore.Exists(workspace);
        var store = SecretStore.Load(workspace);
        var (key, keyCreated) = Machine.LoadOrCreateKey();
        using (key)
        {
            var existing = store.RecipientWith(key.PublicKey);
            var added = existing is null;
            var recipient = existing ?? (settings.Name?.Trim() is { Length: > 0 } name ? name : DefaultName(workspace));
            if (added)
                store.AddRecipient(recipient, key.PublicKey, key);
            store.Save(workspace);

            var notReadable = store.Secrets.Keys.Where(name => !SecretCipher.IsReadableBy(store.Secrets[name], key.KeyId)).ToList();
            var result = new SecretsInitResult(workspace.RelativePath(SecretStore.PathFor(workspace)), fileCreated, Machine.KeySource, keyCreated,
                recipient, key.PublicKey, added, notReadable);
            log.Debug("secrets.init", $"Recipient {recipient} in {result.File}", new Dictionary<string, object?> { ["keyCreated"] = keyCreated, ["added"] = added });

            if (settings.Json)
            {
                Json(result);
                return InfraExitCodes.Success;
            }

            Line(keyCreated ? $"Created key [bold]{Markup.Escape(result.KeySource)}[/]" : $"[grey]Using key {Markup.Escape(result.KeySource)}[/]");
            Line(fileCreated ? $"Created [bold]{Markup.Escape(result.File)}[/]" : $"[grey]Using {Markup.Escape(result.File)}[/]");
            Line(added ? $"Added recipient [bold]{Markup.Escape(recipient)}[/]" : $"[grey]Already a recipient as {Markup.Escape(recipient)}[/]");
            if (notReadable.Count > 0)
                Line($"[yellow]{notReadable.Count} secret(s) are not readable by this key yet.[/] Ask an existing recipient to run: " +
                     Markup.Escape($"infra secrets recipients add \"{recipient}\" {key.PublicKey}"));
            return InfraExitCodes.Success;
        }
    }

    static string DefaultName(InfraWorkspace workspace) =>
        GitIdentity.Author(workspace.Root) ?? $"{Environment.UserName}@{Environment.MachineName}";
}
