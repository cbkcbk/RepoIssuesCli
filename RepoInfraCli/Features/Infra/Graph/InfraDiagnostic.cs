namespace RepoInfraCli.Features.Infra.Graph;

public sealed record InfraDiagnostic(Severity Severity, string Code, string Message, SourceLocation? Location)
{
    public static InfraDiagnostic Error(string code, string message, SourceLocation? location = null) => new(Severity.Error, code, message, location);

    public static InfraDiagnostic Warning(string code, string message, SourceLocation? location = null) => new(Severity.Warning, code, message, location);

    public override string ToString() => Location is null ? $"{Severity}: {Message}" : $"{Location}: {Severity}: {Message}";
}

public static class DiagnosticCodes
{
    public const string ScriptError = "script-error";
    public const string UnknownOption = "unknown-option";
    public const string MissingOption = "missing-option";
    public const string InvalidValue = "invalid-value";
    public const string DuplicateName = "duplicate-name";
    public const string ProtectUnsupported = "protect-unsupported";
    public const string FileOutsideRepo = "file-outside-repo";
    public const string MissingProject = "missing-project";
    public const string DuplicateProject = "duplicate-project";
    public const string InvalidName = "invalid-name";
    public const string EnvRequired = "env-required";
    public const string UnknownEnv = "unknown-env";
    public const string UndefinedHost = "undefined-host";
    public const string OutputMisuse = "output-misuse";
    public const string TaskOutsidePlay = "task-outside-play";
    public const string RequireDenied = "require-denied";
    public const string ContainerConflict = "container-conflict";
    public const string ContainerUnreadable = "container-unreadable";
}
