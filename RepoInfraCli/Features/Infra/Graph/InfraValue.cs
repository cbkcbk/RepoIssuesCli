using System.Collections.Immutable;
using System.Globalization;

namespace RepoInfraCli.Features.Infra.Graph;

public abstract record InfraValue
{
    public const string SecretDisplay = "(secret)";
    public const string UnknownDisplay = "(known after apply)";

    public abstract string Display();

    public IEnumerable<InfraValue> Descendants()
    {
        yield return this;
        var children = this switch
        {
            ListValue list => list.Items,
            MapValue map => map.Entries.Values,
            _ => [],
        };
        foreach (var child in children)
        foreach (var descendant in child.Descendants())
            yield return descendant;
    }

    public IEnumerable<NodeId> References() =>
        Descendants().Select(value => value switch
        {
            OutputRef output => output.Node,
            NodeRef node => node.Node,
            _ => (NodeId?)null,
        }).OfType<NodeId>();
}

public sealed record StringValue(string Value) : InfraValue
{
    public override string Display() => Value;
}

public sealed record NumberValue(double Value) : InfraValue
{
    public override string Display() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record BoolValue(bool Value) : InfraValue
{
    public override string Display() => Value ? "true" : "false";
}

public sealed record ListValue(ImmutableArray<InfraValue> Items) : InfraValue
{
    public static ListValue Empty { get; } = new([]);

    public override string Display() => $"[{string.Join(", ", Items.Select(item => item.Display()))}]";
}

public sealed record MapValue(ImmutableSortedDictionary<string, InfraValue> Entries) : InfraValue
{
    public static MapValue Empty { get; } = new(ImmutableSortedDictionary.Create<string, InfraValue>(StringComparer.Ordinal));

    public InfraValue? this[string key] => Entries.GetValueOrDefault(key);

    public string? GetString(string key) => this[key] is StringValue text ? text.Value : null;

    public bool GetBool(string key) => this[key] is BoolValue { Value: true };

    public MapValue Without(string key) => new(Entries.Remove(key));

    public override string Display() => $"{{{string.Join(", ", Entries.Select(entry => $"{entry.Key} = {entry.Value.Display()}"))}}}";
}

public sealed record SecretRef(string Path) : InfraValue
{
    public override string Display() => SecretDisplay;
}

public sealed record FileRef(string Path, string Resolved) : InfraValue
{
    public override string Display() => Path;
}

public sealed record OutputRef(NodeId Node, string Field, bool Sensitive = false) : InfraValue
{
    public override string Display() => Sensitive ? SecretDisplay : UnknownDisplay;
}

public sealed record NodeRef(NodeId Node) : InfraValue
{
    public override string Display() => Node.Value;
}
