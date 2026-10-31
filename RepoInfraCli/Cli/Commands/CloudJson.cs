using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Cli.Commands;

public sealed record LiveResourceView(
    string Provider,
    string Kind,
    string Name,
    string Status,
    string? Id,
    string? Marker,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Outputs)
{
    public static LiveResourceView From(CloudTarget target, string status)
    {
        var live = target.Live;
        var outputs = live?.Outputs.ToDictionary(
            output => output.Key,
            output => target.Handler.SensitiveOutputs.Contains(output.Key) ? InfraValue.SecretDisplay : output.Value) ?? [];
        return new LiveResourceView(target.Provider, target.Kind, target.Name, status, live?.Id, live?.Mark?.Text,
            live?.Fields ?? new Dictionary<string, string>(), outputs);
    }
}

public sealed record ShowView(IReadOnlyList<LiveResourceView> Resources);

public sealed record DestroyView(bool Destroyed, IReadOnlyList<LiveResourceView> Resources, IReadOnlyList<LiveResourceView> Kept, string? Error);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ShowView))]
[JsonSerializable(typeof(LiveResourceView))]
[JsonSerializable(typeof(DestroyView))]
[JsonSerializable(typeof(string))]
public partial class CloudJsonContext : JsonSerializerContext;

public static class CloudJson
{
    public static string Serialize<T>(T value) => RepoJson.Serialize(value, CloudJsonContext.Default.Options);
}
