using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoCli.Core.Features.IssueLinks;

sealed record IssueFile
{
    public int? Number { get; init; }

    public string? Name { get; init; }

    public string? Status { get; init; }

    public List<int>? BlockedBy { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IssueFile))]
partial class IssueFileJsonContext : JsonSerializerContext;
