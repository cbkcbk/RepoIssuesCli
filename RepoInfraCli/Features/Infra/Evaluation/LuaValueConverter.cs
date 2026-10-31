using System.Collections.Immutable;
using NuLua;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed class LuaValueConverter(ILuaState state)
{
    public const string ResolvedField = "__resolved";
    public const string FieldTag = "__field";
    public const string SensitiveTag = "__sensitive";

    public InfraValue? Convert(LuaValue value) => value.Type switch
    {
        LuaValueType.String => new StringValue(value.Read<string>()),
        LuaValueType.Number => new NumberValue(value.Read<double>()),
        LuaValueType.Boolean => new BoolValue(value.Read<bool>()),
        LuaValueType.Table => ConvertTable(value),
        _ => null,
    };

    public MapValue ConvertMap(LuaValue value) => Convert(value) as MapValue ?? MapValue.Empty;

    InfraValue ConvertTable(LuaValue value)
    {
        if (Text(value, Shapes.SecretTag) is { } secret)
            return new SecretRef(secret);
        if (Text(value, Shapes.FileTag) is { } file)
            return new FileRef(file, Text(value, ResolvedField) ?? file);
        if (Text(value, Shapes.OutputTag) is not null && Text(value, Shapes.NodeTag) is { } outputNode)
            return new OutputRef(new NodeId(outputNode), Text(value, FieldTag) ?? "", !LuaTables.Raw(state, value, SensitiveTag).IsNil);
        if (Text(value, Shapes.NodeTag) is { } node)
            return new NodeRef(new NodeId(node));

        var pairs = LuaTables.Pairs(value.Read<LuaTable>());
        if (LuaTables.IsList(pairs))
            return new ListValue([.. LuaTables.ListItems(pairs).Select(Convert).OfType<InfraValue>()]);

        var entries = ImmutableSortedDictionary.CreateBuilder<string, InfraValue>(StringComparer.Ordinal);
        foreach (var (key, item) in pairs)
        {
            if (Convert(item) is { } converted)
                entries[LuaTables.KeyText(key)] = converted;
        }
        return new MapValue(entries.ToImmutable());
    }

    string? Text(LuaValue value, string key) => LuaTables.RawString(state, value, key);
}
