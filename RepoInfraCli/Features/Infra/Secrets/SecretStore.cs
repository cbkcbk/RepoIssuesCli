using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed record RecipientChange(string Recipient, IReadOnlyList<string> Secrets, IReadOnlyList<string> Skipped);

public sealed partial class SecretStore
{
    public const string FileName = "secrets.yaml";
    const string Heading = "# Managed by 'infra secrets'. Names are plain, values are encrypted per recipient.";

    public SortedDictionary<string, string> Recipients { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public static string PathFor(InfraWorkspace workspace) => Path.Combine(workspace.Folder, FileName);

    public static bool Exists(InfraWorkspace workspace) => File.Exists(PathFor(workspace));

    public static SecretStore Load(InfraWorkspace workspace)
    {
        var path = PathFor(workspace);
        return File.Exists(path) ? Parse(File.ReadAllText(path), workspace.RelativePath(path)) : new SecretStore();
    }

    public static SecretStore Parse(string yaml, string source = FileName)
    {
        Dictionary<string, Dictionary<string, string>?>? document;
        try
        {
            document = new DeserializerBuilder().Build().Deserialize<Dictionary<string, Dictionary<string, string>?>?>(yaml);
        }
        catch (YamlException exception)
        {
            throw new UsageException($"{source} is not valid: {exception.Message}");
        }

        var store = new SecretStore();
        foreach (var (section, entries) in document ?? [])
        {
            var target = section switch
            {
                "recipients" => store.Recipients,
                "secrets" => store.Secrets,
                _ => throw new UsageException($"{source} has an unknown section '{section}'; expected recipients and secrets."),
            };
            foreach (var (name, value) in entries ?? [])
                target[name] = value;
        }
        return store;
    }

    public bool Save(InfraWorkspace workspace)
    {
        var path = PathFor(workspace);
        var text = Serialize();
        if (File.Exists(path) && File.ReadAllText(path) == text)
            return false;

        Directory.CreateDirectory(workspace.Folder);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return true;
    }

    public string Serialize()
    {
        var text = new StringBuilder().Append(Heading).Append('\n');
        Section(text, "recipients", Recipients);
        Section(text, "secrets", Secrets);
        return text.ToString();
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static string RequireValidName(string name) => IsValidName(name)
        ? name
        : throw new UsageException($"'{name}' is not a valid secret name; use segments of letters, digits, '.', '_' or '-' separated by '/' (e.g. db/password or prod/db/password).");

    public string? RecipientWith(string publicKey) =>
        Recipients.FirstOrDefault(entry => entry.Value == publicKey).Key;

    public IReadOnlyList<string> Readers(string name)
    {
        var ids = SecretCipher.KeyIds(Secrets[name]);
        return [.. Recipients.Where(entry => ids.Contains(RecipientKey.Id(entry.Value))).Select(entry => entry.Key)];
    }

    public void Set(string name, string value)
    {
        if (Recipients.Count == 0)
            throw new UsageException($"{FileName} has no recipients; run 'infra secrets init' first.");
        Secrets[name] = SecretCipher.Encrypt(name, value, Recipients.Values);
    }

    public SecretReveal Reveal(string name, SecretKey? key)
    {
        if (!Secrets.TryGetValue(name, out var encoded))
            return SecretReveal.Missing;
        if (key is null)
            return SecretReveal.NoKey;
        try
        {
            return SecretCipher.Decrypt(name, encoded, key) is { } value ? SecretReveal.Of(value) : SecretReveal.NotRecipient;
        }
        catch (CryptographicException)
        {
            return SecretReveal.Tampered;
        }
    }

    public RecipientChange AddRecipient(string name, string publicKey, SecretKey? key)
    {
        var normalized = RecipientKey.Normalize(publicKey);
        if (Recipients.TryGetValue(name, out var existing) && existing != normalized)
            throw new UsageException($"Recipient '{name}' already exists with a different key; remove it first.");
        if (RecipientWith(normalized) is { } other && other != name)
            throw new UsageException($"That key already belongs to recipient '{other}'.");

        Recipients[name] = normalized;
        var shared = new List<string>();
        var skipped = new List<string>();
        foreach (var secret in Secrets.Keys.ToList())
        {
            if (SecretCipher.IsReadableBy(Secrets[secret], RecipientKey.Id(normalized)))
                continue;
            if (key is not null && SecretCipher.Share(secret, Secrets[secret], key, normalized) is { } updated)
            {
                Secrets[secret] = updated;
                shared.Add(secret);
            }
            else
                skipped.Add(secret);
        }
        return new RecipientChange(name, shared, skipped);
    }

    public RecipientChange RemoveRecipient(string name, SecretKey? key)
    {
        if (!Recipients.Remove(name, out var publicKey))
            throw new SecretNotFoundException($"No recipient named '{name}'.");
        if (Recipients.Count == 0)
        {
            Recipients[name] = publicKey;
            throw new UsageException($"'{name}' is the last recipient; add another recipient before removing it.");
        }

        var removedId = RecipientKey.Id(publicKey);
        var rotate = new List<string>();
        var stripped = new List<string>();
        foreach (var secret in Secrets.Keys.ToList())
        {
            var encoded = Secrets[secret];
            if (!SecretCipher.IsReadableBy(encoded, removedId))
                continue;

            rotate.Add(secret);
            if (key is not null && TryDecrypt(secret, encoded, key) is { } value)
                Secrets[secret] = SecretCipher.Encrypt(secret, value, Recipients.Values);
            else
            {
                Secrets[secret] = SecretCipher.Unshare(encoded, removedId);
                stripped.Add(secret);
            }
        }
        return new RecipientChange(name, rotate, stripped);
    }

    static string? TryDecrypt(string name, string encoded, SecretKey key)
    {
        try
        {
            return SecretCipher.Decrypt(name, encoded, key);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    static void Section(StringBuilder text, string title, SortedDictionary<string, string> entries)
    {
        if (entries.Count == 0)
        {
            text.Append(title).Append(": {}\n");
            return;
        }

        text.Append(title).Append(":\n");
        foreach (var (name, value) in entries)
            text.Append("  ").Append(Scalar(name)).Append(": ").Append(Scalar(value)).Append('\n');
    }

    static readonly string[] YamlKeywords = ["null", "true", "false", "yes", "no", "on", "off"];

    static string Scalar(string value) => PlainPattern().IsMatch(value) && !YamlKeywords.Contains(value, StringComparer.OrdinalIgnoreCase)
        ? value
        : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/+=-]*(:[A-Za-z0-9+/=]+)?$")]
    private static partial Regex PlainPattern();
}

public sealed record SecretReveal(SecretRevealStatus Status, string? Value)
{
    public static SecretReveal Missing { get; } = new(SecretRevealStatus.Missing, null);

    public static SecretReveal NoKey { get; } = new(SecretRevealStatus.NoKey, null);

    public static SecretReveal NotRecipient { get; } = new(SecretRevealStatus.NotRecipient, null);

    public static SecretReveal Tampered { get; } = new(SecretRevealStatus.Tampered, null);

    public static SecretReveal Of(string value) => new(SecretRevealStatus.Revealed, value);
}

public enum SecretRevealStatus
{
    Revealed,
    Missing,
    NoKey,
    NotRecipient,
    Tampered,
}

public sealed class SecretNotFoundException(string message) : NotFoundException(message);
