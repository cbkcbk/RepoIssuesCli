using RepoDocsCli.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli;

public static class DocsApp
{
    public static CommandApp Create(IAnsiConsole output, IAnsiConsole errors)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            RepoCliApp.Configure(config, "docs", typeof(DocsApp).Assembly, output, errors);

            config.AddCommand<InitCommand>("init")
                .WithExample("init", "--json");
        });
        return app;
    }
}
