using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra.Hooks;

public enum GuardDecision
{
    Allow,
    Ask,
    Deny,
}

public sealed record GuardVerdict(GuardDecision Decision, string Reason)
{
    public static readonly GuardVerdict Allowed = new(GuardDecision.Allow, "");

    public static GuardVerdict Changes(string command) =>
        new(GuardDecision.Ask, $"`infra {command}` can change live infrastructure, databases or secrets. Approve it only after reviewing the plan or the command's target.");
}

/// <summary>Decides whether a Bash or PowerShell command line may run <c>infra</c> without asking the user.</summary>
public static partial class ShellGuard
{
    static readonly HashSet<string> PromptingModes = new(StringComparer.Ordinal) { "default", "auto" };
    static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal) { "init", "validate", "plan", "show", "hook", "generate-plugin" };
    static readonly HashSet<string> ChangingCommands = new(StringComparer.Ordinal) { "apply", "adopt", "destroy" };
    static readonly HashSet<string> ReadOnlySecrets = new(StringComparer.Ordinal) { "list", "key", "generate" };
    static readonly HashSet<string> HelpOptions = new(StringComparer.Ordinal) { "-h", "--help", "--version" };
    static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase) { "sudo", "env", "time", "exec", "nohup", "command", "call", "." };

    static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "pwsh", "powershell", "pwsh.exe", "powershell.exe", "cmd", "cmd.exe", "wsl", "eval", "iex", "invoke-expression", "invoke-command",
        "start-process", "xargs",
    };

    const string SecretShow =
        "`infra secrets show` prints a decrypted secret, and Claude must never see secret values. If the user needs the value, ask them to run it themselves.";

    const string Unclassified =
        "This command runs infra in a way the infra plugin can't classify (through a shell, a variable or a dotnet run without `--`), so it may change live infrastructure.";

    public static GuardVerdict Decide(string command, string? permissionMode)
    {
        var verdict = Classify(command);
        if (verdict.Decision != GuardDecision.Ask || permissionMode is { } mode && PromptingModes.Contains(mode))
            return verdict;

        return new GuardVerdict(GuardDecision.Deny,
            $"{verdict.Reason} This session's permission mode ({permissionMode ?? "unknown"}) may run it without asking, so ask the user to run it themselves: `! {command.Trim()}`");
    }

    public static GuardVerdict Classify(string command)
    {
        var joined = Continuation().Replace(command, " ");
        var segments = Separator().Split(joined).Select(Tokens).Where(tokens => tokens.Count > 0).ToList();
        var verdicts = segments.Select(Segment).ToList();

        var mentionsInfra = segments.Any(tokens => tokens.Any(token => IsInfra(token) || token.Contains("RepoInfraCli", StringComparison.OrdinalIgnoreCase)));
        if (mentionsInfra && segments.Any(tokens => Launchers.Contains(tokens[0]) || tokens[0].StartsWith('$')))
            verdicts.Add(new GuardVerdict(GuardDecision.Ask, Unclassified));

        return verdicts.MaxBy(verdict => verdict.Decision) is { Decision: not GuardDecision.Allow } strictest ? strictest : GuardVerdict.Allowed;
    }

    static GuardVerdict Segment(List<string> tokens)
    {
        if (IsInfra(tokens[0]))
            return Arguments(tokens[1..]);
        if (!tokens[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return GuardVerdict.Allowed;

        if (tokens.FindIndex(1, IsInfra) is var tool and > 0)
            return Arguments(tokens[(tool + 1)..]);
        if (tokens.Count > 1 && tokens[1].EndsWith("RepoInfraCli.dll", StringComparison.OrdinalIgnoreCase))
            return Arguments(tokens[2..]);
        if (tokens.Contains("run") && tokens.Any(token => token.Contains("RepoInfraCli", StringComparison.OrdinalIgnoreCase)))
            return tokens.IndexOf("--") is var separator and > 0 ? Arguments(tokens[(separator + 1)..]) : new GuardVerdict(GuardDecision.Ask, Unclassified);
        return GuardVerdict.Allowed;
    }

    static GuardVerdict Arguments(List<string> arguments)
    {
        if (arguments.Count == 0 || arguments.Any(HelpOptions.Contains))
            return GuardVerdict.Allowed;

        var verdict = Command(arguments);
        return verdict.Decision == GuardDecision.Allow && arguments.Contains("--accept-new-hosts")
            ? new GuardVerdict(GuardDecision.Ask, "`--accept-new-hosts` trusts and records SSH host keys infra has never seen.")
            : verdict;
    }

    static GuardVerdict Command(List<string> arguments)
    {
        var command = arguments[0];
        var sub = arguments.Count > 1 ? arguments[1] : null;
        return command switch
        {
            _ when ReadOnlyCommands.Contains(command) => GuardVerdict.Allowed,
            _ when ChangingCommands.Contains(command) => GuardVerdict.Changes(command),
            "restore" => arguments.Contains("--list") ? GuardVerdict.Allowed : GuardVerdict.Changes(command),
            "db" => sub is null ? GuardVerdict.Allowed : GuardVerdict.Changes($"db {sub}"),
            "secrets" => sub switch
            {
                null => GuardVerdict.Allowed,
                "show" => new GuardVerdict(GuardDecision.Deny, SecretShow),
                _ when ReadOnlySecrets.Contains(sub) => GuardVerdict.Allowed,
                _ => GuardVerdict.Changes($"secrets {sub}"),
            },
            _ => GuardVerdict.Changes(command),
        };
    }

    static List<string> Tokens(string segment)
    {
        var tokens = segment.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('"', '\''))
            .Where(token => token.Length > 0)
            .ToList();
        var start = tokens.FindIndex(token => !Wrappers.Contains(token) && !Assignment().IsMatch(token));
        return start < 0 ? [] : tokens[start..];
    }

    static bool IsInfra(string token)
    {
        var name = token[(token.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.Equals("infra", StringComparison.OrdinalIgnoreCase) || name.Equals("infra.exe", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"`\r?\n|\\\r?\n")]
    private static partial Regex Continuation();

    [GeneratedRegex(@"&&|\|\||\$\(|[;|&\n(){}`]")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();
}
