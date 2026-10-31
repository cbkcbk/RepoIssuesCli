using System.Text.Json;

namespace RepoInfraCli.Features.Infra.Cloud;

public static class JsonPaths
{
    public static JsonElement? At(this JsonElement element, params ReadOnlySpan<string> path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return null;
        }
        return current;
    }

    public static string? Text(this JsonElement element, params ReadOnlySpan<string> path) =>
        element.At(path) is { } value
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    public static IReadOnlyList<JsonElement> Items(this JsonElement element, params ReadOnlySpan<string> path) =>
        element.At(path) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];

    public static IReadOnlyList<string> Strings(this JsonElement element, params ReadOnlySpan<string> path) =>
        [.. element.Items(path).Select(item => item.Text()).OfType<string>()];
}
