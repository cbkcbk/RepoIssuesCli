using System.Reflection;
using RepoIssuesCli.Cli.Commands;
using RepoIssuesCli.Features.Issues;
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
            config.SetApplicationName("issues");
            config.SetApplicationVersion(typeof(IssuesApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");
            config.ConfigureConsole(output);
            config.SetExceptionHandler((exception, _) => HandleException(exception, errors));

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
            config.AddCommand<Plugin.GeneratePluginCommand>(Plugin.GeneratePluginCommand.Name);
#endif
        });
        return app;
    }

    static int HandleException(Exception exception, IAnsiConsole errors)
    {
        if (exception.InnerException is UsageException usage)
            exception = usage;

        if (exception is CommandAppException { Pretty: { } pretty })
        {
            errors.Write(pretty);
            return ExitCodes.Usage;
        }

        errors.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)}");
        return exception switch
        {
            UsageException or AmbiguousIssueReferenceException or IssueFileConflictException or CommandAppException => ExitCodes.Usage,
            IssueNotFoundException => ExitCodes.NotFound,
            _ => ExitCodes.Error,
        };
    }
}
