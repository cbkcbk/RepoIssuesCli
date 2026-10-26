using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;

namespace RepoIssuesCli.Features.Issues;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Issue))]
[JsonSerializable(typeof(List<Issue>))]
[JsonSerializable(typeof(List<ValidationProblem>))]
[JsonSerializable(typeof(RenumberResult))]
[JsonSerializable(typeof(InitResult))]
public partial class IssueJsonContext : JsonSerializerContext;

public static class IssueJson
{
    public static JsonSerializerOptions FileOptions { get; } = RepoJson.FileOptions(IssueJsonContext.Default);

    public static JsonSerializerOptions OutputOptions => IssueJsonContext.Default.Options;

    public static string Serialize<T>(T value) => RepoJson.Serialize(value, FileOptions);

    public static string SerializeOutput<T>(T value) => RepoJson.Serialize(value, OutputOptions);

    public static T? Deserialize<T>(string json) => RepoJson.Deserialize<T>(json, FileOptions);
}