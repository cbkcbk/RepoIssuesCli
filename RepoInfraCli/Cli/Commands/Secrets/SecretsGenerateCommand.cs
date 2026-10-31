using System.ComponentModel;
using System.Security.Cryptography;
using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands.Secrets;

public sealed class SecretsGenerateSettings : InfraSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Secret name, optionally prefixed with an env (prod/db/password).")]
    public string Name { get; init; } = "";

    [CommandOption("--length <LENGTH>")]
    [Description("Number of characters (8-4096). Default 32.")]
    [DefaultValue(32)]
    public int Length { get; init; } = 32;

    [CommandOption("--force")]
    [Description("Replace an existing value.")]
    public bool Force { get; init; }
}

[Description("Store a random URL-safe value (A-Z a-z 0-9 - _) as a secret without printing it. Refuses to replace an existing secret unless --force.")]
public sealed class SecretsGenerateCommand(IAnsiConsole console, ErrorConsole errors, SecretEnvironment machine)
    : SecretsCommand<SecretsGenerateSettings>(console, errors, machine)
{
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    protected override int Run(InfraWorkspace workspace, SecretsGenerateSettings settings, InfraLog log)
    {
        RejectEnv(settings);
        var name = SecretStore.RequireValidName(settings.Name);
        if (settings.Length is < 8 or > 4096)
            throw new UsageException("--length must be between 8 and 4096.");

        var store = RequireStore(workspace);
        var created = !store.Secrets.ContainsKey(name);
        if (!created && !settings.Force)
        {
            log.Error("secrets.exists", $"Secret '{name}' already exists; pass --force to replace it.");
            return InfraExitCodes.Invalid;
        }

        var value = RandomNumberGenerator.GetString(Alphabet, settings.Length);
        log.AddSecret(value);
        store.Set(name, value);
        store.Save(workspace);
        var result = new SecretWriteResult(name, created, [.. store.Recipients.Keys]);

        if (settings.Json)
            Json(result);
        else
            Line($"Generated [bold]{Markup.Escape(name)}[/] ({settings.Length} characters) for {result.Recipients.Count} recipient(s); reveal with 'infra secrets show {Markup.Escape(name)}'");
        return InfraExitCodes.Success;
    }
}
