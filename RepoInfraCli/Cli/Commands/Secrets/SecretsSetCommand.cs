using System.ComponentModel;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands.Secrets;

public sealed class SecretsSetSettings : InfraSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Secret name as used in infra.luau (db/password), or prefixed with an env (prod/db/password).")]
    public string Name { get; init; } = "";

    [CommandOption("--stdin")]
    [Description("Read the value from stdin (one trailing newline is dropped). Values are never taken from arguments.")]
    public bool Stdin { get; init; }
}

[Description("Encrypt a secret for every recipient. The value comes from a hidden prompt or --stdin, never from the command line.")]
public sealed class SecretsSetCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine, TextReader input)
    : SecretsCommand<SecretsSetSettings>(console, errors, machine)
{
    protected override int Run(InfraWorkspace workspace, SecretsSetSettings settings, InfraLog log)
    {
        RejectEnv(settings);
        var name = SecretStore.RequireValidName(settings.Name);
        var store = RequireStore(workspace);
        var value = settings.Stdin ? TrimNewline(input.ReadToEnd()) : Prompt(settings, name);
        if (value.Length == 0)
            throw new UsageException("The secret value is empty.");

        log.AddSecret(value);
        var created = !store.Secrets.ContainsKey(name);
        store.Set(name, value);
        store.Save(workspace);
        var result = new SecretWriteResult(name, created, [.. store.Recipients.Keys]);
        log.Debug("secrets.set", $"Set {name}", new Dictionary<string, object?> { ["created"] = created });

        if (settings.Json)
            Json(result);
        else
            Line($"{(created ? "Added" : "Updated")} [bold]{Markup.Escape(name)}[/] for {result.Recipients.Count} recipient(s)");
        return InfraExitCodes.Success;
    }

    string Prompt(InfraSettings settings, string name)
    {
        new Interaction(Console, settings).EnsureInteractive("--stdin (or run interactively)");
        return Console.Prompt(new TextPrompt<string>($"Value for [bold]{Markup.Escape(name)}[/]:").Secret());
    }

    static string TrimNewline(string text) =>
        text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;
}
