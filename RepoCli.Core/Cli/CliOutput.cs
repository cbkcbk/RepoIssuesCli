using Spectre.Console;

namespace RepoCli.Core.Cli;

public static class CliOutput
{
    public static void WriteJson(IAnsiConsole console, string json) => console.Profile.Out.Writer.Write(json);

    public static void WriteUnwrappedLine(IAnsiConsole console, string markup)
    {
        console.Write(new Unwrapped(new Markup(markup)));
        console.WriteLine();
    }
}
