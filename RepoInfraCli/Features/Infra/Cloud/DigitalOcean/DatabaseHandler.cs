using System.Text.Json;
using System.Text.Json.Nodes;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud.DigitalOcean;

public sealed class DatabaseHandler : ResourceHandler
{
    public const string KindName = "database";
    public const string AdminConnectionStringField = "admin_connection_string";
    const string MissingUser = "(none)";

    public override string Provider => DigitalOceanModule.ProviderKind;

    public override string Kind => KindName;

    public override string ParentKind => DatabaseClusterHandler.KindName;

    public override bool HasMarker => false;

    public override bool Prunable => false;

    public override IReadOnlySet<string> SensitiveOutputs { get; } = new HashSet<string> { "connection_string", AdminConnectionStringField };

    public override IReadOnlyList<FieldSpec> Fields { get; } = [new("user")];

    public override string Display(ResourceNode node) => node.Id.Value[(node.Id.Value.IndexOf(':') + 1)..];

    public override LiveResource? FindOwned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker) => FindExisting(node, pool);

    public override LiveResource? FindUnowned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker) => null;

    public override Task<LiveResource> BindAsync(ProviderSession session, ResourceNode node, LiveResource live, LiveResource? parent, CancellationToken cancellationToken)
    {
        var user = User(node);
        var password = parent?.Raw.Items("users").FirstOrDefault(entry => entry.Text("name") == user) is { ValueKind: JsonValueKind.Object } entry ? entry.Text("password") : null;
        if (password is null && node.Existing)
            throw new ResourcePlanException($"user {user} was not found in cluster {parent?.Name}");
        return Task.FromResult(WithUser(session, live, parent, user, password));
    }

    static LiveResource WithUser(ProviderSession session, LiveResource live, LiveResource? cluster, string user, string? password)
    {
        var outputs = new Dictionary<string, string>();
        if (password is not null && cluster is not null)
            outputs["connection_string"] = session.Sensitive(ConnectionString(cluster, live.Name, user, session.Sensitive(password)));
        if (cluster?.Raw.Text("connection", "user") is { } admin && cluster.Raw.Text("connection", "password") is { } adminPassword)
            outputs[AdminConnectionStringField] = session.Sensitive(
                ConnectionString(cluster, cluster.Raw.Text("connection", "database") ?? "defaultdb", admin, session.Sensitive(adminPassword)));
        return live with
        {
            Fields = new Dictionary<string, string> { ["user"] = password is null ? MissingUser : user },
            Outputs = outputs,
        };
    }

    public static string ConnectionString(LiveResource cluster, string database, string user, string password) =>
        $"postgresql://{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(password)}@{cluster.Outputs.GetValueOrDefault("host")}:{cluster.Outputs.GetValueOrDefault("port")}/{Uri.EscapeDataString(database)}?sslmode=require";

    public override IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values) =>
        new Dictionary<string, string?> { ["user"] = User(node) };

    static string User(ResourceNode node) => node.Options.GetString("user") ?? node.Name;

    public override async Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken)
    {
        var cluster = Cluster(context);
        await context.Session.DigitalOcean.SendAsync(HttpMethod.Post, $"v2/databases/{cluster.Id}/dbs", new JsonObject { ["name"] = context.Node.Name }, cancellationToken);
        var live = new LiveResource(KindName, $"{cluster.Id}/{context.Node.Name}", context.Node.Name, null) { Parent = cluster.Id };
        return await EnsureUserAsync(context, cluster, live, cancellationToken);
    }

    public override Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken) =>
        EnsureUserAsync(context, Cluster(context), live, cancellationToken);

    static async Task<LiveResource> EnsureUserAsync(ResourceContext context, LiveResource cluster, LiveResource live, CancellationToken cancellationToken)
    {
        var user = User(context.Node);
        var client = context.Session.DigitalOcean;
        var password = cluster.Raw.Items("users").FirstOrDefault(entry => entry.Text("name") == user) is { ValueKind: JsonValueKind.Object } existing
            ? existing.Text("password")
            : (await client.PostAsync($"v2/databases/{cluster.Id}/users", new JsonObject { ["name"] = user }, "user", cancellationToken)).Text("password");
        return WithUser(context.Session, live, cluster, user, password);
    }

    static LiveResource Cluster(ResourceContext context) =>
        context.Parent ?? throw new CloudApiException($"database {context.Node.Name} needs its cluster, which is not live");

    public override Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        session.DigitalOcean.DeleteAsync($"v2/databases/{live.Parent}/dbs/{Uri.EscapeDataString(live.Name)}", cancellationToken);
}
