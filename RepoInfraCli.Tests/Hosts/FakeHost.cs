using System.Collections.Concurrent;
using System.Collections.Immutable;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Hosts;

namespace RepoInfraCli.Tests.Hosts;

public sealed record Upload(string Path, byte[] Content, string? Mode, string? Owner);

public sealed class FakeHost(HostNode host) : IHostConnection
{
    readonly List<(string Fragment, CommandResult Result)> rules = [];

    public HostNode Host => host;

    public HostFacts Facts { get; init; } = new("ubuntu", "24.04", "noble");

    public ConcurrentQueue<string> Scripts { get; } = new();

    public ConcurrentQueue<Upload> Uploads { get; } = new();

    public bool Disposed { get; private set; }

    public FakeHost On(string fragment, string stdout, int exitCode = 0, string stderr = "")
    {
        rules.Add((fragment, new CommandResult(exitCode, stdout, stderr)));
        return this;
    }

    public Task<CommandResult> RunAsync(string script, ReadOnlyMemory<byte> stdin = default, CancellationToken cancellationToken = default)
    {
        Scripts.Enqueue(script);
        var match = rules.LastOrDefault(rule => script.Contains(rule.Fragment, StringComparison.Ordinal));
        return Task.FromResult(match.Result ?? new CommandResult(0, "", ""));
    }

    public Task UploadAsync(string path, ReadOnlyMemory<byte> content, string? mode, string? owner, CancellationToken cancellationToken = default)
    {
        Uploads.Enqueue(new Upload(path, content.ToArray(), mode, owner));
        return Task.CompletedTask;
    }

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Files.GetValueOrDefault(path));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public static HostNode Node(string name = "web", bool become = false, SecretRef? becomePassword = null, InfraValue? address = null, int? port = null, string user = "root") =>
        new(NodeId.Of("host", name), name, address ?? new StringValue("10.0.0.1"), user, port, become, becomePassword, null, false, null, null);
}

public sealed class FakeConnector : IHostConnector
{
    readonly Dictionary<string, Func<HostNode, FakeHost>> hosts = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> failures = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, FakeHost> Opened { get; } = new();

    public ConcurrentQueue<(string Host, HostConnectOptions Options)> Connects { get; } = new();

    public FakeConnector Host(string name, Action<FakeHost>? setup = null)
    {
        hosts[name] = node =>
        {
            var fake = new FakeHost(node);
            setup?.Invoke(fake);
            return fake;
        };
        return this;
    }

    public FakeConnector Fail(string name, string message)
    {
        failures[name] = message;
        return this;
    }

    public Task<IHostConnection> ConnectAsync(HostNode host, HostConnectOptions options, CancellationToken cancellationToken = default)
    {
        Connects.Enqueue((host.Name, options));
        if (failures.TryGetValue(host.Name, out var message))
            throw new HostException(host.Name, message);
        var fake = hosts.TryGetValue(host.Name, out var create) ? create(host) : new FakeHost(host);
        Opened[host.Name] = fake;
        return Task.FromResult<IHostConnection>(fake);
    }
}

public static class Options
{
    public static MapValue Map(params (string Key, InfraValue Value)[] entries) =>
        new(entries.Aggregate(ImmutableSortedDictionary.Create<string, InfraValue>(StringComparer.Ordinal), (map, entry) => map.Add(entry.Key, entry.Value)));

    public static ListValue List(params string[] items) => new([.. items.Select(item => (InfraValue)new StringValue(item))]);

    public static StringValue Text(string value) => new(value);

    public static BoolValue Flag(bool value) => new(value);

    public static NumberValue Number(double value) => new(value);

    public static TaskNode Task(string kind, string? name, MapValue options, HostNode? host = null, NodeId? notifies = null, string? id = null) =>
        new(new NodeId(id ?? $"task:{kind}:{name}"), (host ?? FakeHost.Node()).Id, kind, name, options, null, notifies);
}
