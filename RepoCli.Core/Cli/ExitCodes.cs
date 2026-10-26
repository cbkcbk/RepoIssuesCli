using System.ComponentModel;

namespace RepoCli.Core.Cli;

public static class ExitCodes
{
    [Description("success")]
    public const int Success = 0;

    [Description("validation found errors, or warnings with --strict")]
    public const int ValidationFailed = 1;

    [Description("usage error or missing input")]
    public const int Usage = 2;

    [Description("referenced item not found")]
    public const int NotFound = 3;

    [Description("unexpected error")]
    public const int Error = 4;
}

public class UsageException(string message) : Exception(message);

public abstract class NotFoundException(string message) : Exception(message);
