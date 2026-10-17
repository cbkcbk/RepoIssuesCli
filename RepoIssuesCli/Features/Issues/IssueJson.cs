using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RepoIssuesCli.Features.Issues;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(List<Issue>))]
[JsonSerializable(typeof(List<ValidationProblem>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class IssueJsonContext : JsonSerializerContext;

public static class IssueJson
{
    public static JsonSerializerOptions FileOptions { get; } = new(IssueJsonContext.Default.Options)
    {
        TypeInfoResolver = IssueJsonContext.Default.WithAddedModifier(SkipEmptyCollections),
    };

    public static JsonSerializerOptions OutputOptions => IssueJsonContext.Default.Options;

    public static string Serialize<T>(T value) => Write(value, FileOptions);

    public static string SerializeOutput<T>(T value) => Write(value, OutputOptions);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)FileOptions.GetTypeInfo(typeof(T)));

    static string Write<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T))) + "\n";

    static void SkipEmptyCollections(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
                continue;

            property.ShouldSerialize = (_, value) => value is IEnumerable items && items.GetEnumerator().MoveNext();
        }
    }
}
