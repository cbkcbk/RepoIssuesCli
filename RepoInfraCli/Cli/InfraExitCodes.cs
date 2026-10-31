using System.ComponentModel;

namespace RepoInfraCli.Cli;

public static class InfraExitCodes
{
    [Description("success")]
    public const int Success = ExitCodes.Success;

    [Description("blocked or invalid: validation errors, protected resources or a refused change")]
    public const int Invalid = ExitCodes.ValidationFailed;

    [Description("usage error or missing input")]
    public const int Usage = ExitCodes.Usage;

    [Description("referenced item not found")]
    public const int NotFound = ExitCodes.NotFound;

    [Description("unexpected error")]
    public const int Unexpected = ExitCodes.Error;

    [Description("partially applied: some changes were made before a failure")]
    public const int PartiallyApplied = 5;

    [Description("drift: plan --exit-code found changes")]
    public const int Drift = 6;
}
