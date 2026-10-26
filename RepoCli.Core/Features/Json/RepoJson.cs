using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RepoCli.Core.Features.Json;

public static class RepoJson
{
    public static JsonSerializerOptions FileOptions(JsonSerializerContext context) => new(context.Options)
    {
        TypeInfoResolver = context.WithAddedModifier(SkipEmptyCollections),
    };

    public static string Serialize<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T))) + "\n";

    public static T? Deserialize<T>(string json, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));

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
