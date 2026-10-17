namespace RepoIssuesCli.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int ValidationFailed = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Error = 4;
}

public sealed class UsageException(string message) : Exception(message);
