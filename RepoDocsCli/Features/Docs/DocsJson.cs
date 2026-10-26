using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoDocsCli.Features.ClaudeMd;

namespace RepoDocsCli.Features.Docs;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Feature))]
[JsonSerializable(typeof(List<Feature>))]
[JsonSerializable(typeof(InitResult))]
[JsonSerializable(typeof(RenderResult))]
[JsonSerializable(typeof(PlanProgress))]
public partial class DocsJsonContext : JsonSerializerContext;

public static class DocsJson
{
    public static JsonSerializerOptions OutputOptions => DocsJsonContext.Default.Options;

    public static string SerializeOutput<T>(T value) => RepoJson.Serialize(value, OutputOptions);
}