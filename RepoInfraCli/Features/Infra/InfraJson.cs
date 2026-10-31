using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra;

public sealed record DiagnosticView(string Code, string Message, string? File, int? Line)
{
    public static DiagnosticView From(InfraDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Message, diagnostic.Location?.File, diagnostic.Location?.Line);
}

public sealed record GraphSummary(int Resources, int Hosts, int Plays, int Apps, int Tasks, int Secrets)
{
    public static GraphSummary From(InfraGraph graph) =>
        new(graph.Resources.Count, graph.Hosts.Count, graph.Plays.Count, graph.Apps.Count, graph.Tasks.Count(), graph.Secrets.Count);
}

public sealed record ValidateResult(bool Valid, IReadOnlyList<DiagnosticView> Errors, IReadOnlyList<DiagnosticView> Warnings, GraphSummary? Summary);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InfraInitResult))]
[JsonSerializable(typeof(ValidateResult))]
public partial class InfraJsonContext : JsonSerializerContext;

public static class InfraJson
{
    public static string SerializeOutput<T>(T value) => RepoJson.Serialize(value, InfraJsonContext.Default.Options);
}
