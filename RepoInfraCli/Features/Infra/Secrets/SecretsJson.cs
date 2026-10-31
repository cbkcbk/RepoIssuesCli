using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;

namespace RepoInfraCli.Features.Infra.Secrets;

public sealed record SecretsInitResult(string File, bool FileCreated, string KeySource, bool KeyCreated, string Recipient, string PublicKey, bool RecipientAdded, IReadOnlyList<string> NotReadable);

public sealed record SecretWriteResult(string Name, bool Created, IReadOnlyList<string> Recipients);

public sealed record SecretShowResult(string Name, string Value);

public sealed record SecretsKeyResult(string PublicKey, string Source, string? Recipient);

public sealed record RecipientAddedResult(string Recipient, string PublicKey, IReadOnlyList<string> Shared, IReadOnlyList<string> NotShared);

public sealed record RecipientRemovedResult(string Recipient, IReadOnlyList<string> Rotate, IReadOnlyList<string> NotRewrapped);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SecretsInitResult))]
[JsonSerializable(typeof(SecretWriteResult))]
[JsonSerializable(typeof(SecretShowResult))]
[JsonSerializable(typeof(SecretsKeyResult))]
[JsonSerializable(typeof(SecretInventory))]
[JsonSerializable(typeof(RecipientAddedResult))]
[JsonSerializable(typeof(RecipientRemovedResult))]
public partial class SecretsJsonContext : JsonSerializerContext;

public static class SecretsJson
{
    public static string Serialize<T>(T value) => RepoJson.Serialize(value, SecretsJsonContext.Default.Options);
}
