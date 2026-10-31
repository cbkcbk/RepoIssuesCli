using System.ComponentModel;
using RepoInfraCli.Features.Infra.Plan;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli;

public class HostRunSettings : InfraSettings
{
    [CommandOption("--play <NAME>")]
    [Description("Only this play. Repeat for more plays.")]
    public string[] Plays { get; init; } = [];

    [CommandOption("--host <NAME>")]
    [Description("Only this host. Repeat for more hosts.")]
    public string[] Hosts { get; init; } = [];

    [CommandOption("--hosts-only")]
    [Description("Skip cloud resources and plan only host plays.")]
    public bool HostsOnly { get; init; }

    [CommandOption("--resources-only")]
    [Description("Plan only cloud resources and skip host plays.")]
    public bool ResourcesOnly { get; init; }

    [CommandOption("--accept-new-hosts")]
    [Description("Trust and record unknown SSH host keys in ~/.ssh/known_hosts. Changed keys always fail.")]
    public bool AcceptNewHosts { get; init; }

    [CommandOption("--allow-dirty")]
    [Description("Build app images from a working tree with uncommitted changes; they are tagged <sha>-dirty-<timestamp>.")]
    public bool AllowDirty { get; init; }

    public PlanFilter Filter => new(Plays, Hosts, HostsOnly) { ResourcesOnly = ResourcesOnly };
}

public sealed class PlanSettings : HostRunSettings
{
    [CommandOption("--exit-code")]
    [Description("Exit with 6 when the plan has changes (shell tasks and deferred items don't count).")]
    public bool ExitCode { get; init; }
}

public sealed class ApplySettings : HostRunSettings
{
    [CommandOption("-y|--yes")]
    [Description("Apply without asking for confirmation. Required without a terminal.")]
    public bool Yes { get; init; }

    [CommandOption("--allow-destroy")]
    [Description("Allow deleting or replacing cloud resources (prune, -/+). Protected and exported resources are never destroyed.")]
    public bool AllowDestroy { get; init; }
}

public sealed class AdoptSettings : InfraSettings
{
    [CommandArgument(0, "[NAMES]")]
    [Description("Resources to adopt, as name or kind:name. Default: every declared resource that exists without a marker.")]
    public string[] Names { get; init; } = [];

    [CommandOption("-y|--yes")]
    [Description("Adopt without asking for confirmation. Required without a terminal.")]
    public bool Yes { get; init; }
}

public sealed class ShowSettings : InfraSettings
{
    [CommandArgument(0, "[NAME]")]
    [Description("A resource name or kind:name. Default: list every resource.")]
    public string? Name { get; init; }

    [CommandOption("--field <FIELD>")]
    [Description("Print only this live field or output (for example ipv4_address), unredacted, for use in scripts.")]
    public string? Field { get; init; }
}

public sealed class DestroySettings : InfraSettings
{
    [CommandArgument(0, "[NAMES]")]
    [Description("Resources to destroy, as name or kind:name. Default: every resource carrying this project's marker.")]
    public string[] Names { get; init; } = [];

    [CommandOption("-y|--yes")]
    [Description("Destroy without a prompt; needs --confirm with the owner name.")]
    public bool Yes { get; init; }

    [CommandOption("--confirm <OWNER>")]
    [Description("The owner name (<project> or <project>-<env>) to confirm a non-interactive destroy.")]
    public string? Confirm { get; init; }
}