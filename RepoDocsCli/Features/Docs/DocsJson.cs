using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoDocsCli.Features.Affected;
using RepoDocsCli.Features.Check;
using RepoDocsCli.Features.ClaudeMd;
using RepoDocsCli.Features.Import;
using RepoDocsCli.Features.Prune;
using RepoDocsCli.Features.Queries;

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
[JsonSerializable(typeof(TouchResult))]
[JsonSerializable(typeof(ImportResult))]
[JsonSerializable(typeof(List<ValidationProblem>))]
[JsonSerializable(typeof(CheckReport))]
[JsonSerializable(typeof(FeatureView))]
[JsonSerializable(typeof(EntryView))]
[JsonSerializable(typeof(ForResult))]
[JsonSerializable(typeof(List<SearchHit>))]
[JsonSerializable(typeof(List<FeatureListItem>))]
[JsonSerializable(typeof(DocsItemResult))]
[JsonSerializable(typeof(List<PruneCandidate>))]
[JsonSerializable(typeof(AffectedResult))]
public partial class DocsJsonContext : JsonSerializerContext;

public static class DocsJson
{
    public static JsonSerializerOptions OutputOptions => DocsJsonContext.Default.Options;

    public static string SerializeOutput<T>(T value) => RepoJson.Serialize(value, OutputOptions);
}