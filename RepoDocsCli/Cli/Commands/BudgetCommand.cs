using System.ComponentModel;
using RepoDocsCli.Features.Budget;
using RepoDocsCli.Features.Docs;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

public enum BudgetGrouping
{
    Feature,
    Kind,
}

[Description("Estimate the token cost of the always-loaded docs (CLAUDE.md, its generated block and @imports) against on-demand entries and hook pushes. Read-only.")]
public sealed class BudgetCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<BudgetCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--by <GROUP>")]
        [Description("Show on-demand costs only per feature or only per kind. Both by default; --json always has both.")]
        [TypeConverter(typeof(EnumOptionConverter<BudgetGrouping>))]
        public BudgetGrouping? By { get; init; }

        [CommandOption("--top <N>")]
        [Description("Number of largest entries to list as condensing targets. Defaults to 10.")]
        public int Top { get; init; } = DocsBudget.DefaultTop;

        public override ValidationResult Validate() =>
            Top < 0 ? ValidationResult.Error("--top must be 0 or more.") : ValidationResult.Success();
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        var snapshot = store.IsInitialized ? LoadReportingErrors(store) : null;
        var report = DocsBudget.Measure(store.Root, snapshot, settings.Top);

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(report));
        else
            BudgetRenderer.Write(Console, report, settings.By);
        return ExitCodes.Success;
    }
}
