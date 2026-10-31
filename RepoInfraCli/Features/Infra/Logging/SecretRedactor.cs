namespace RepoInfraCli.Features.Infra.Logging;

public interface ISecretRedactor
{
    void AddSecret(string value);

    string Redact(string text);
}

public sealed class SecretRedactor : ISecretRedactor
{
    public const string Replacement = "(secret)";

    readonly Lock gate = new();
    string[] secrets = [];

    public void AddSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        lock (gate)
        {
            if (secrets.Contains(value, StringComparer.Ordinal))
                return;
            secrets = [.. secrets.Append(value).OrderByDescending(secret => secret.Length)];
        }
    }

    public string Redact(string text)
    {
        var current = secrets;
        foreach (var secret in current)
            text = text.Replace(secret, Replacement, StringComparison.Ordinal);
        return text;
    }
}
