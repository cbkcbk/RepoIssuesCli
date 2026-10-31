using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Cloud;

public sealed record ResourceContext(ProviderSession Session, ResourceNode Node, LiveResource? Parent, CloudValues Values, OwnerMark Mark, Marker Marker);

public abstract class ResourceHandler
{
    public const string MarkerField = "marker";

    public abstract string Provider { get; }

    public abstract string Kind { get; }

    public virtual string? ParentKind => null;

    public virtual IReadOnlyList<FieldSpec> Fields => [];

    public virtual IReadOnlySet<string> SensitiveOutputs { get; } = new HashSet<string>();

    public virtual bool HasMarker => true;

    public virtual bool UniqueNames => false;

    public virtual bool Prunable => true;

    public virtual bool LookupOnly => false;

    public virtual int DestroyRank => 9;

    public virtual string LiveName(ResourceNode node, Marker marker) => node.Name;

    public virtual string Display(ResourceNode node) => node.Name;

    public virtual string Display(LiveResource live) => live.Mark?.Name ?? live.Name;

    public virtual Task<IReadOnlyList<LiveResource>> ListAsync(ProviderSession session, Marker marker, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Kind} is listed through its parent");

    public virtual IReadOnlyList<LiveResource> ChildrenOf(LiveResource parent) => [.. parent.Children.Where(child => child.Kind == Kind)];

    public virtual Task<LiveResource> BindAsync(ProviderSession session, ResourceNode node, LiveResource live, LiveResource? parent, CancellationToken cancellationToken) =>
        Task.FromResult(live);

    public virtual LiveResource? FindOwned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker) =>
        pool.FirstOrDefault(live => live.Mark is { } mark && mark.Owner == marker.Owner && mark.Name == node.Name);

    public virtual LiveResource? FindUnowned(ResourceNode node, IEnumerable<LiveResource> pool, Marker marker) =>
        pool.FirstOrDefault(live => live.Mark is null && (live.Name == node.Name || live.Name == LiveName(node, marker)));

    public virtual LiveResource? FindExisting(ResourceNode node, IEnumerable<LiveResource> pool) =>
        pool.FirstOrDefault(live => live.Name == node.Name);

    public virtual IReadOnlyDictionary<string, string?> Desired(ResourceNode node, CloudValues values) => new Dictionary<string, string?>();

    public virtual Task<LiveResource> CreateAsync(ResourceContext context, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Kind} is looked up, never created");

    public virtual Task<LiveResource> UpdateAsync(ResourceContext context, LiveResource live, IReadOnlySet<string> changed, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Kind} can't be changed");

    public virtual Task DeleteAsync(ProviderSession session, LiveResource live, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Kind} can't be deleted");

    public virtual Task<LiveResource> MarkAsync(ProviderSession session, LiveResource live, OwnerMark mark, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Kind} carries no ownership marker");

    public virtual string AdoptText(LiveResource live, OwnerMark mark, Marker marker) => $"mark {live.Id} as {mark.Text}";

    protected static string Option(ResourceNode node, string key) => node.Options.GetString(key) ?? "";
}
