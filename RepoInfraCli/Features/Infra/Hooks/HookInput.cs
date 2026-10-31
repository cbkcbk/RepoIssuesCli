using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoInfraCli.Features.Infra.Hooks;

public enum InfraHookEvent
{
    Shell,
    Edit,
}

/// <summary>The fields of a Claude Code hook's stdin JSON that the infra hooks use.</summary>
public sealed record HookInput(string? Cwd, string? Command, string? FilePath, string? PermissionMode)
{
    public static HookInput? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return null;

            return new HookInput(
                Text(root["cwd"]),
                Text(root["tool_input"]?["command"]),
                Text(root["tool_input"]?["file_path"]),
                Text(root["permission_mode"]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
