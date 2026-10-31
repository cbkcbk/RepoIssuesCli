using RepoInfraCli.Cli;
using Spectre.Console;

var errors = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
return await InfraApp.Create(AnsiConsole.Console, errors, Console.In).RunAsync(args);
