using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Cli.Commands.Secrets;

[Description("List recipients and secret names with who can decrypt each; never values. Flags secrets infra.luau references but the file lacks, and stored secrets nothing references. Without --env every env in .repo-infra/envs/ is checked.")]
public sealed class SecretsListCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine)
    : SecretsCommand<InfraSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, InfraSettings settings, InfraLog log)
    {
        var store = RequireStore(workspace);
        using var key = Machine.LoadKey();
        var inventory = SecretInventory.Build(workspace, store, key, settings.Env);
        foreach (var note in inventory.Notes)
            log.Record(InfraLogLevel.Warning, "secrets.list.note", note);

        if (settings.Json)
        {
            Json(inventory);
            return InfraExitCodes.Success;
        }

        Line($"[bold]Recipients[/] ({inventory.Recipients.Count})");
        foreach (var recipient in inventory.Recipients)
            Line($"  {Markup.Escape(recipient.Name)}{(recipient.Self ? " [green](this machine)[/]" : "")}");

        Line($"[bold]Secrets[/] ({inventory.Secrets.Count})");
        foreach (var secret in inventory.Secrets)
            Line($"  {Markup.Escape(secret.Name)}  [grey]{Markup.Escape(string.Join(", ", secret.Readers))}[/]{Flags(secret)}");

        foreach (var missing in inventory.Missing)
            Line($"[red]missing[/] {Markup.Escape(missing.Path)}{(missing.Env is null ? "" : $" [grey](env {Markup.Escape(missing.Env)})[/]")}: {Markup.Escape(missing.Command)}");
        foreach (var note in inventory.Notes)
            Line($"[yellow]Note:[/] {Markup.Escape(note)}");
        return InfraExitCodes.Success;
    }

    static string Flags(SecretEntry secret) => string.Concat(
        secret.Problem is { } problem ? $" [red]({Markup.Escape(problem)})[/]" : "",
        secret.Readable ? "" : " [yellow](not readable here)[/]",
        secret.Referenced == false ? " [yellow](not referenced)[/]" : "");
}
