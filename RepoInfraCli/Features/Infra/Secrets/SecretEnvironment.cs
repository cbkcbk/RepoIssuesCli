namespace RepoInfraCli.Features.Infra.Secrets;

public sealed record SecretEnvironment(Func<string, string?> Variables, string KeyFile)
{
    public const string KeyVariable = "REPO_INFRA_KEY";

    public static SecretEnvironment Default { get; } = new(Environment.GetEnvironmentVariable, DefaultKeyFile());

    public static string DefaultKeyFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "repo-infra", "key");

    public bool UsesVariable => !string.IsNullOrWhiteSpace(Variables(KeyVariable));

    public string KeySource => UsesVariable ? KeyVariable : KeyFile;

    public SecretKey? LoadKey()
    {
        if (Variables(KeyVariable) is { } variable && !string.IsNullOrWhiteSpace(variable))
            return Parse(variable, KeyVariable);
        return File.Exists(KeyFile) ? Parse(File.ReadAllText(KeyFile), KeyFile) : null;
    }

    public SecretKey RequireKey() =>
        LoadKey() ?? throw new UsageException($"No repo-infra key on this machine ({KeyFile}) and {KeyVariable} is not set; run 'infra secrets init'.");

    public (SecretKey Key, bool Created) LoadOrCreateKey()
    {
        if (LoadKey() is { } existing)
            return (existing, false);

        var key = SecretKey.Generate();
        WriteKeyFile(key.Export());
        return (key, true);
    }

    void WriteKeyFile(string text)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(KeyFile))!;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(folder);
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(KeyFile, System.Text.Encoding.ASCII, options))
            writer.Write(text + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static SecretKey Parse(string text, string source)
    {
        try
        {
            return SecretKey.Parse(text);
        }
        catch (FormatException exception)
        {
            throw new UsageException($"{source}: {exception.Message}");
        }
    }
}
