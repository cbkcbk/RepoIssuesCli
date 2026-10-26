using RepoIssuesCli.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoIssuesCli.Cli;

public static class IssuesApp
{
    public static CommandApp Create(IAnsiConsole output, IAnsiConsole errors)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            RepoCliApp.Configure(config, "issues", typeof(IssuesApp).Assembly, output, errors);
            config.Settings.Registrar.RegisterInstance(new ErrorConsole(errors));

            config.AddCommand<InitCommand>("init");
            config.AddCommand<NewCommand>("new").WithAlias("add")
                .WithExample("new", "\"Login fails on empty password\"", "-p", "High", "-t", "bug", "-l", "src/Auth/Login.cs:42:9");
            config.AddCommand<ListCommand>("list").WithAlias("ls")
                .WithExample("list", "--ready", "--json");
            config.AddCommand<ShowCommand>("show")
                .WithExample("show", "3");
            config.AddCommand<EditCommand>("edit")
                .WithExample("edit", "3", "--status", "InProgress", "--add-blocker", "1");
            config.AddCommand<StatusCommand>("status")
                .WithExample("status", "3", "Done");
            config.AddCommand<CommentCommand>("comment")
                .WithExample("comment", "3", "\"Reproduced on main\"");
            config.AddCommand<DeleteCommand>("delete").WithAlias("rm")
                .WithExample("delete", "3", "--force");
            config.AddCommand<RenumberCommand>("renumber")
                .WithExample("renumber", "\"Add password rules\"");
            config.AddCommand<ValidateCommand>("validate");
#if DEBUG
            config.AddCommand<RepoCli.Core.Cli.Plugin.GeneratePluginCommand<Plugin.IssuesPlugin>>(RepoCli.Core.Cli.Plugin.PluginGenerator.CommandName);
#endif
        });
        return app;
    }
}