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
            config.AddCommand<CheckCommand>("check")
                .WithAlias("validate")
                .WithExample("check", "--strict")
                .WithExample("check", "--fix");
            config.AddCommand<TouchCommand>("touch")
                .WithExample("touch", "billing/rounding")
                .WithExample("touch", "--json");
            config.AddCommand<MergeDriverCommand>("merge-driver")
                .WithExample("merge-driver", "%O", "%A", "%B", "%P");
        });
        return app;
    }
}