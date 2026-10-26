using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoCli.Core.Cli;

public static class RepoCliApp
{
    public static void Configure(IConfigurator config, string name, Assembly toolAssembly, IAnsiConsole output, IAnsiConsole errors)
    {
        config.SetApplicationName(name);
        config.SetApplicationVersion(Version(toolAssembly));
        config.ConfigureConsole(output);
        config.SetExceptionHandler((exception, _) => HandleException(exception, errors));
    }

    public static string Version(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

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
            UsageException or CommandAppException => ExitCodes.Usage,
            NotFoundException => ExitCodes.NotFound,
            _ => ExitCodes.Error,
        };
    }
}
