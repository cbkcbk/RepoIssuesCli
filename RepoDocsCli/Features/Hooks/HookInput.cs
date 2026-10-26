using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoDocsCli.Features.Hooks;

public enum HookEvent
{
    SessionStart,
    Read,
    Edit,
    Stop,
}

/// <summary>The fields of a Claude Code hook's stdin JSON that the docs hooks use.</summary>
public sealed record HookInput(string? SessionId, string? Cwd, string? FilePath, bool StopHookActive)
{
    public static HookInput? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return null;

            return new HookInput(
                Text(root["session_id"]),
                Text(root["cwd"]),
                Text(root["tool_input"]?["file_path"]),
                root["stop_hook_active"] is JsonValue active && active.TryGetValue<bool>(out var value) && value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
}
