using System.ComponentModel;
using Spectre.Console.Cli;

namespace RepoCli.Core.Cli;

public class GlobalSettings : CommandSettings
{
    [CommandOption("--root <PATH>")]
    [Description("Repository root. Defaults to the nearest folder containing .repo-issues, .repo-docs or .git.")]
    public string? Root { get; init; }

    [CommandOption("--json")]
    [Description("Write machine-readable JSON to stdout. Implies --no-interactive.")]
    public bool Json { get; init; }

    [CommandOption("--no-interactive")]
    [Description("Never prompt; fail when required input is missing. Also enabled by ISSUES_NONINTERACTIVE=1 or DOCS_NONINTERACTIVE=1.")]
    public bool NoInteractive { get; init; }
}
