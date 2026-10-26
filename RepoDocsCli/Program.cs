using RepoDocsCli.Cli;
using Spectre.Console;

var errors = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
return await DocsApp.Create(AnsiConsole.Console, errors).RunAsync(args);
