using System.ComponentModel;

namespace RepoIssuesCli.Cli;

public static class ExitCodes
{
    [Description("success")]
    public const int Success = 0;

    [Description("validate found errors, or warnings with --strict")]
    public const int ValidationFailed = 1;

    [Description("usage error or missing input")]
    public const int Usage = 2;

    [Description("issue not found")]
    public const int NotFound = 3;

    [Description("unexpected error")]
    public const int Error = 4;
}

public sealed class UsageException(string message) : Exception(message);
