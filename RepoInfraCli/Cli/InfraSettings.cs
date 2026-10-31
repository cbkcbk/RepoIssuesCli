using System.ComponentModel;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli;

public class InfraSettings : GlobalSettings
{
    [CommandOption("--env <ENV>")]
    [Description("Environment to evaluate (an entry in .repo-infra/envs/). Required when that folder exists.")]
    public string? Env { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Print more detail: -v for steps and the graph outline, -vv for debug. Secrets are always redacted.")]
    public bool Verbose { get; init; }

    [CommandOption("--log <PATH>")]
    [Description("Append every event as a JSON line to this file. Secrets are redacted.")]
    public string? Log { get; init; }
}
