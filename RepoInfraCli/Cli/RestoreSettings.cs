using System.ComponentModel;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli;

public sealed class RestoreSettings : InfraSettings
{
    [CommandArgument(0, "<DATABASE>")]
    [Description("The postgres.database name in infra.luau (for example api), not a compose service.")]
    public string Database { get; init; } = "";

    [CommandOption("--list")]
    [Description("List the dumps of the database and exit.")]
    public bool List { get; init; }

    [CommandOption("--dump <KEY>")]
    [Description("Restore this dump (a key from --list). Default: the newest dump.")]
    public string? Dump { get; init; }

    [CommandOption("--at <TIME>")]
    [Description("Restore the newest dump taken at or before this time (UTC unless it has an offset), for example 2026-10-30T03:00Z.")]
    public string? At { get; init; }

    [CommandOption("--into <NAME>")]
    [Description("Restore into this new database on the same server instead of over the live one; use it to verify a dump or run a drill.")]
    public string? Into { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Restore without typing the database name. Required without a terminal.")]
    public bool Yes { get; init; }

    [CommandOption("--accept-new-hosts")]
    [Description("Trust and record unknown SSH host keys in ~/.ssh/known_hosts. Changed keys always fail.")]
    public bool AcceptNewHosts { get; init; }
}
