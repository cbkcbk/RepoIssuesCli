using System.Text.Json;
using System.Text.Json.Serialization;
using RepoCli.Core.Features.Json;
using RepoInfraCli.Features.Infra.Postgres;

namespace RepoInfraCli.Cli.Commands;

public sealed record DumpView(string Key, long Size, DateTimeOffset Time)
{
    public static DumpView From(BackupDump dump) => new(dump.Key, dump.Size, dump.Time);
}

public sealed record DumpListView(string Database, string Host, string Location, IReadOnlyList<DumpView> Dumps);

public sealed record RestoreView(string Database, string Host, DumpView Dump, string Target, bool OverLive, IReadOnlyList<string> Apps, bool Restored);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DumpListView))]
[JsonSerializable(typeof(RestoreView))]
public partial class RestoreJsonContext : JsonSerializerContext;

public static class RestoreJson
{
    public static string Serialize<T>(T value) => RepoJson.Serialize(value, RestoreJsonContext.Default.Options);
}
