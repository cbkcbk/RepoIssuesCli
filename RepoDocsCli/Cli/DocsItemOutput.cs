using RepoDocsCli.Features.Docs;
using Spectre.Console;

namespace RepoDocsCli.Cli;

public static class DocsItemOutput
{
    public static void Write(IAnsiConsole console, bool json, string verb, DocsItemResult result)
    {
        if (json)
            CliOutput.WriteJson(console, DocsJson.SerializeOutput(result));
        else
            CliOutput.WriteUnwrappedLine(console, $"{verb} [bold]{Markup.Escape(result.Address)}[/] [grey]({Markup.Escape(result.File)})[/]");
    }

    public static void WriteDetails(IAnsiConsole console, string address, object item) =>
        console.Write(new Panel(new Text(DocsYaml.Serialize(item).TrimEnd())).Header(Markup.Escape(address)).Expand());
}
