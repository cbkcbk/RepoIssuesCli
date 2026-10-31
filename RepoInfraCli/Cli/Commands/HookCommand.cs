using System.ComponentModel;
using RepoInfraCli.Features.Infra.Hooks;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli.Commands;

[Description("Claude Code hook entry point for the infra plugin: reads the hook JSON on stdin and writes the hook's JSON answer. Events: shell, edit. Always exits 0 and prints nothing when there is nothing to say.")]
public sealed class HookCommand(IAnsiConsole console, TextReader input) : Command<HookCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<event>")]
        [Description("shell (ask or deny before infra commands that change things), edit (validate .repo-infra Luau offline after an edit).")]
        [TypeConverter(typeof(EnumOptionConverter<InfraHookEvent>))]
        public InfraHookEvent Event { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (InfraHooks.Handle(settings.Event, HookInput.Parse(input.ReadToEnd().TrimStart('﻿')), settings.Root) is { } output)
                CliOutput.WriteJson(console, output.ToJsonString() + "\n");
        }
        catch (Exception exception) when (settings.Event == InfraHookEvent.Shell)
        {
            CliOutput.WriteJson(console, InfraHooks.Refuse($"The infra plugin's guard failed ({exception.Message}), so it can't tell whether this command changes live infrastructure.").ToJsonString() + "\n");
        }
        catch (Exception)
        {
            // An edit hook must never break the agent's session.
        }

        return InfraExitCodes.Success;
    }
}