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
            config.Settings.Registrar.RegisterInstance(new ErrorConsole(errors));

            config.AddCommand<InitCommand>("init")
                .WithExample("init", "--json");
            config.AddCommand<RenderCommand>("render")
                .WithExample("render", "--check");
            config.AddCommand<ImportCommand>("import")
                .WithExample("import", "--dry-run")
                .WithExample("import", "Web/CLAUDE.md", "--json");
        });
        return app;
    }
}
