using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Logging;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;

namespace RepoInfraCli.Features.Infra.Hosts;

public sealed class BecomePasswords(ISecretResolver secrets, InfraWorkspace workspace, string? env, IAnsiConsole console, bool canPrompt, InfraLog log)
{
    readonly Dictionary<string, string> known = new(StringComparer.Ordinal);
    readonly SemaphoreSlim gate = new(1, 1);

    public static string SecretPath(HostNode host) => $"hosts/{host.Name}/sudo";

    public static string MissingMessage(HostNode host) =>
        $"sudo on '{host.Name}' needs a password: set become_password = secret \"{SecretPath(host)}\" on the host and provide that secret, or run interactively (without --yes or --no-interactive) to be prompted";

    public BecomePasswordProvider Provider => GetAsync;

    public async Task<string> GetAsync(HostNode host, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (known.TryGetValue(host.Name, out var cached))
                return cached;

            var value = Resolve(host);
            log.AddSecret(value);
            known[host.Name] = value;
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    string Resolve(HostNode host)
    {
        if (host.BecomePassword is { } secret)
        {
            var resolution = secrets.Resolve(workspace, env, [secret]);
            return resolution.Values.TryGetValue(secret.Path, out var value)
                ? value
                : throw new HostException(host.Name,
                    $"become_password for host '{host.Name}': {SecretProblems.Diagnostics(resolution, env)[0].Message}");
        }

        if (!canPrompt)
            throw new HostException(host.Name, MissingMessage(host));

        return console.Prompt(new TextPrompt<string>($"sudo password for {Markup.Escape(host.User)}@{Markup.Escape(host.Name)}:").Secret());
    }
}