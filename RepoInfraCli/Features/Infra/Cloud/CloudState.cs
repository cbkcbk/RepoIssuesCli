using System.Collections.Concurrent;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Plan;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed class CloudState
{
    readonly ConcurrentDictionary<NodeId, LiveResource> live = new();
    readonly ConcurrentDictionary<NodeId, bool> pending = new();
    readonly ConcurrentDictionary<NodeId, bool> created = new();

    public IReadOnlyList<ResourceDiff> Diffs { get; set; } = [];

    public void Reset()
    {
        live.Clear();
        pending.Clear();
        Diffs = [];
    }

    public LiveResource? Live(NodeId id) => pending.ContainsKey(id) ? null : live.GetValueOrDefault(id);

    public void Found(NodeId id, LiveResource resource) => live[id] = resource;

    public void Pend(NodeId id) => pending[id] = true;

    public void Applied(NodeId id, LiveResource resource, bool isNew)
    {
        live[id] = resource;
        pending.TryRemove(id, out _);
        if (isNew)
            created[id] = true;
    }

    public bool CreatedThisRun(NodeId id) => created.ContainsKey(id);

    public string? Output(NodeId id, string field) => Live(id) is { } resource && resource.Outputs.TryGetValue(field, out var value) ? value : null;
}

public sealed class CloudLiveValues(CloudState state) : ILiveValues
{
    public InfraValue? Resolve(OutputRef output) => state.Output(output.Node, output.Field) is { } value ? new StringValue(value) : null;

    public bool CreatedThisRun(NodeId resource) => state.CreatedThisRun(resource);
}

public sealed class CloudValues(CloudState state)
{
    public string? Text(InfraValue value) => value switch
    {
        StringValue text => text.Value,
        NumberValue or BoolValue => value.Display(),
        OutputRef output => state.Output(output.Node, output.Field),
        FileRef file when File.Exists(file.Resolved) => File.ReadAllText(file.Resolved).Trim(),
        FileRef file => throw new ResourcePlanException($"file {file.Path} does not exist ({file.Resolved})"),
        _ => throw new ResourcePlanException($"{value.Display()} can't be used here"),
    };

    public IReadOnlyList<string?> Items(InfraValue? value) => value is ListValue list ? [.. list.Items.Select(Text)] : [];

    public string Require(InfraValue value, string what) =>
        Text(value) ?? throw new CloudApiException($"{what} is still unknown; the resource it comes from was not applied");

    public IReadOnlyList<string> RequireItems(InfraValue? value, string what) =>
        value is ListValue list ? [.. list.Items.Select(item => Require(item, what))] : [];

    public static string? Join(IEnumerable<string?> items)
    {
        var list = items.ToList();
        return list.Any(item => item is null) ? null : $"[{string.Join(", ", list.Order(StringComparer.Ordinal))}]";
    }

    public static string Flag(MapValue options, string key) => options.GetBool(key) ? "true" : "false";
}
