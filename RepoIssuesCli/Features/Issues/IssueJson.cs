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
    public static JsonSerializerOptions Options { get; } = new(IssueJsonContext.Default.Options)
    {
        TypeInfoResolver = IssueJsonContext.Default.WithAddedModifier(SkipEmptyCollections),
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T))) + "\n";

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

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
