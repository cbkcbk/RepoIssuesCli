using RepoIssuesCli.Cli;
using Spectre.Console;

var errors = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
return await RiApp.Create(AnsiConsole.Console, errors).RunAsync(args);
