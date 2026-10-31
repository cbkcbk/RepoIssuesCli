using NuLua;

namespace RepoInfraCli.Features.Infra.Evaluation;

public static class LuaTables
{
    public static IReadOnlyList<KeyValuePair<LuaValue, LuaValue>> Pairs(LuaTable table)
    {
        var pairs = new List<KeyValuePair<LuaValue, LuaValue>>();
        foreach (var pair in table)
            pairs.Add(pair);
        return pairs;
    }

    public static LuaValue Raw(ILuaState state, LuaTable table, string key)
    {
        var top = state.GetTop();
        try
        {
            state.Push(LuaValue.FromTable(table));
            state.PushString(key);
            state.RawGet(-2);
            return state.ToLuaValue(-1);
        }
        finally
        {
            state.SetTop(top);
        }
    }

    public static LuaValue Raw(ILuaState state, LuaValue value, string key) =>
        value.Type == LuaValueType.Table ? Raw(state, value.Read<LuaTable>(), key) : LuaValue.Nil;

    public static string? RawString(ILuaState state, LuaValue value, string key) =>
        Raw(state, value, key) is { Type: LuaValueType.String } text ? text.Read<string>() : null;

    public static bool IsList(IReadOnlyList<KeyValuePair<LuaValue, LuaValue>> pairs)
    {
        if (pairs.Count == 0)
            return false;

        var indexes = new HashSet<long>();
        foreach (var (key, _) in pairs)
        {
            if (key.Type != LuaValueType.Number)
                return false;
            var number = key.Read<double>();
            if (number < 1 || number != Math.Floor(number))
                return false;
            indexes.Add((long)number);
        }
        return indexes.Count == pairs.Count && indexes.Max() == pairs.Count;
    }

    public static IReadOnlyList<LuaValue> ListItems(IReadOnlyList<KeyValuePair<LuaValue, LuaValue>> pairs) =>
        [.. pairs.OrderBy(pair => pair.Key.Read<double>()).Select(pair => pair.Value)];

    public static string KeyText(LuaValue key) => key.Type switch
    {
        LuaValueType.String => key.Read<string>(),
        LuaValueType.Number => key.Read<double>().ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => key.ToString(),
    };
}
