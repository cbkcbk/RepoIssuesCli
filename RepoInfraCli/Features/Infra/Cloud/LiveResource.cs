using System.Text.Json;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed record LiveResource(string Kind, string Id, string Name, OwnerMark? Mark)
{
    static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Fields { get; init; } = None;

    public IReadOnlyDictionary<string, string> Outputs { get; init; } = None;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<LiveResource> Children { get; init; } = [];

    public string? Parent { get; init; }

    public JsonElement Raw { get; init; }

    public bool Is(LiveResource other) => Kind == other.Kind && Id == other.Id;
}

public enum FieldMode
{
    Update,
    Replace,
    CreateOnly,
}

public sealed record FieldSpec(string Name, FieldMode Mode = FieldMode.Update, bool Sensitive = false, string? Note = null);

public sealed class ResourcePlanException(string message) : Exception(message);

public sealed class CloudApiException(string message, int? status = null) : Exception(message)
{
    public int? Status => status;
}