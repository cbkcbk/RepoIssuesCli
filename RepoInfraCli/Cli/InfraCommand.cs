using RepoInfraCli.Features.Infra;
using RepoInfraCli.Features.Infra.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli;

public sealed record ErrorConsole(IAnsiConsole Console);

public abstract class InfraCommand<TSettings>(IAnsiConsole console, ErrorConsole errors) : Command<TSettings>
    where TSettings : InfraSettings
{
    protected IAnsiConsole Console => console;

    protected IAnsiConsole Errors => errors.Console;

    public override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        using var log = InfraLog.Create(errors.Console, settings.Verbose ? Verbosity(context.Arguments) : 0, settings.Log, context.Name, settings.Env);
        using var current = log.UseAsCurrent();
        log.Debug("command.start", $"infra {context.Name}");
        var exitCode = Run(InfraWorkspace.Open(settings.Root), settings, log);
        log.Debug("command.end", $"infra {context.Name} exited with {exitCode}", new Dictionary<string, object?> { ["exitCode"] = exitCode });
        return exitCode;
    }

    protected abstract int Run(InfraWorkspace workspace, TSettings settings, InfraLog log);

    protected static void RequireInitialized(InfraWorkspace workspace)
    {
        if (!workspace.IsInitialized)
            throw new UsageException($"No {RepoLocator.InfraFolderName}/{InfraWorkspace.EntryFileName} in {workspace.Root}; run 'infra init' first.");
    }

    public static int Verbosity(IEnumerable<string> arguments) =>
        Math.Max(1, arguments.Sum(argument => argument switch
        {
            "--verbose" => 1,
            ['-', 'v', ..] when argument.Skip(1).All(letter => letter == 'v') => argument.Length - 1,
            _ => 0,
        }));
}