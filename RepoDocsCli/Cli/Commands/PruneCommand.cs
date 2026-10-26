using System.ComponentModel;
using System.Globalization;
using RepoDocsCli.Features.Docs;
using RepoDocsCli.Features.Prune;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoDocsCli.Cli.Commands;

[Description("List stale-docs candidates with reasons and a suggested action: dead or changed anchors and paths, orphaned docs, unread entries, plans of shipped features and done plan steps. Read-only; always exits 0.")]
public sealed class PruneCommand(IAnsiConsole console, ErrorConsole errors) : DocsCommand<PruneCommand.Settings>(console, errors)
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("-r|--reason <CODE>")]
        [Description("Only candidates with one of these reasons; repeatable.")]
        [TypeConverter(typeof(EnumOptionConverter<PruneCode>))]
        public PruneCode[] Reasons { get; init; } = [];

        [CommandOption("-f|--feature <ID>")]
        [Description("Only the feature and its entries (repo for repo.yaml).")]
        public string? Feature { get; init; }

        [CommandOption("--today <DATE>")]
        [Description("Count unread days up to this date (yyyy-MM-dd) instead of today.")]
        public string? Today { get; init; }

        public DateOnly? TodayDate => Today is null ? null : DateOnly.ParseExact(Today, DocsTime.DateFormat, CultureInfo.InvariantCulture);

        public override ValidationResult Validate() =>
            Today is null || DateOnly.TryParseExact(Today, DocsTime.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? ValidationResult.Success()
                : ValidationResult.Error($"--today must be a date like 2026-10-26, not '{Today}'.");
    }

    protected override int Run(DocsStore store, Settings settings)
    {
        RequireInitialized(store);
        var snapshot = LoadReportingErrors(store);
        if (settings.Feature is { } feature && feature != FeatureId.RepoFileStem && snapshot.Find(feature) is null)
            throw new FeatureNotFoundException(feature);

        var today = settings.TodayDate ?? DocsTime.Today(store.Clock);
        var candidates = DocsPruner.Prune(store.Root, snapshot, today, new PruneFilter(settings.Reasons, settings.Feature));

        if (settings.Json)
            CliOutput.WriteJson(Console, DocsJson.SerializeOutput(candidates.ToList()));
        else
            Write(candidates);
        return ExitCodes.Success;
    }

    void Write(IReadOnlyList<PruneCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            Console.MarkupLine("[green]Nothing to prune.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Reason").NoWrap())
            .AddColumn(new TableColumn("Address").NoWrap())
            .AddColumn(new TableColumn("Kind").NoWrap())
            .AddColumn("Details")
            .AddColumn("Suggestion");

        foreach (var candidate in candidates)
            table.AddRow(
                new Markup($"[{Color(candidate.Strongest)}]{candidate.Strongest}[/]"),
                new Markup($"[bold]{Markup.Escape(candidate.Address)}[/]"),
                new Markup(candidate.Kind is { } kind ? Markup.Escape(kind) : "[grey]feature[/]"),
                new Text(string.Join('\n', candidate.Reasons.Select(reason => candidate.Reasons.Count > 1 ? $"{reason.Code}: {reason.Message}" : reason.Message))),
                new Markup($"{Markup.Escape(candidate.Suggestion.Action)}\n[grey]{Markup.Escape(candidate.Suggestion.Command)}[/]"));

        Console.Write(table);
        Console.MarkupLine($"[yellow]{candidates.Count} candidate(s).[/]");
    }

    static string Color(PruneCode code) => code switch
    {
        PruneCode.DeprecatedOrphan or PruneCode.Orphaned or PruneCode.BrokenAnchor => "red",
        PruneCode.ChangedAnchor or PruneCode.DeadPath or PruneCode.ShippedPlan => "yellow",
        _ => "blue",
    };
}
