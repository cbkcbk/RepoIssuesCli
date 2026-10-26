using System.ComponentModel;
using System.Text;
using RepoDocsCli.Features.Hooks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

/// <param name="Reader">The hook input; <c>null</c> reads the process's stdin.</param>
public sealed record HookStdin(TextReader? Reader);

[Description("Claude Code hook entry point for the docs plugin: reads the hook JSON on stdin and writes the hook's JSON answer. Events: session-start, read, edit, stop. Always exits 0 and prints nothing when there is nothing to say.")]
public sealed class HookCommand(IAnsiConsole console, HookStdin stdin) : Command<HookCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<event>")]
        [Description("session-start (record the starting HEAD), read and edit (push matching docs after Read, Edit or Write), stop (block once on affected docs).")]
        [TypeConverter(typeof(EnumOptionConverter<HookEvent>))]
        public HookEvent Event { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (HookInput.Parse(ReadInput()) is { } input && DocsHooks.Handle(settings.Event, input, settings.Root) is { } output)
                CliOutput.WriteJson(console, output.ToJsonString() + "\n");
        }
        catch (Exception)
        {
            // A hook must never break the agent's session.
        }

        return ExitCodes.Success;
    }

    string ReadInput()
    {
        if (stdin.Reader is { } reader)
            return reader.ReadToEnd();
        if (!Console.IsInputRedirected)
            return "";

        using var standardInput = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        return standardInput.ReadToEnd();
    }
}
