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
            config.AddCommand<ShowCommand>("show")
                .WithExample("show", "billing")
                .WithExample("show", "billing/rounding", "--json");
            config.AddCommand<ForCommand>("for")
                .WithExample("for", "src/Billing/Invoice.cs", "--json");
            config.AddCommand<FindCommand>("find")
                .WithExample("find", "\"vat rounding\"", "--limit", "5");
            config.AddCommand<ListCommand>("list").WithAlias("ls")
                .WithExample("list", "--status", "in-progress");
            config.AddCommand<TouchCommand>("touch")
                .WithExample("touch", "billing/rounding")
                .WithExample("touch", "--json");
            config.AddCommand<AffectedCommand>("affected")
                .WithExample("affected", "--json")
                .WithExample("affected", "--since", "origin/main", "--strict");
            config.AddCommand<MergeDriverCommand>("merge-driver")
                .WithExample("merge-driver", "%O", "%A", "%B", "%P");
            config.AddCommand<ImportCommand>("import")
                .WithExample("import", "--dry-run")
                .WithExample("import", "Web/CLAUDE.md", "--json");
        });
        return app;
    }
}