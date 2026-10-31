using NuLua;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed record ShapeProblem(string Code, string Message);

public sealed class ShapeChecker(ILuaState state)
{
    static readonly string[] Tags = [Shapes.SecretTag, Shapes.FileTag, Shapes.OutputTag, Shapes.HandlerTag, Shapes.BuildTag, Shapes.RoleTag, Shapes.KindTag];

    public IReadOnlyList<ShapeProblem> Check(LuaValue value, Shape shape, string path = "")
    {
        var problems = new List<ShapeProblem>();
        Visit(value, shape, path, problems);
        return problems;
    }

    public bool Matches(LuaValue value, Shape shape) => Check(value, shape).Count == 0;

    void Visit(LuaValue value, Shape shape, string path, List<ShapeProblem> problems)
    {
        switch (shape)
        {
            case AnyShape:
                return;
            case PrimitiveShape primitive:
                if (value.Type != primitive.Type)
                    problems.Add(Invalid(path, shape, value));
                return;
            case FunctionShape:
                if (value.Type != LuaValueType.Function)
                    problems.Add(Invalid(path, shape, value));
                return;
            case LiteralShape literal:
                if (value.Type != LuaValueType.String || !literal.Values.Contains(value.Read<string>()))
                    problems.Add(Invalid(path, shape, value));
                return;
            case TagShape tag:
                if (LuaTables.Raw(state, value, tag.Tag).IsNil)
                    problems.Add(Invalid(path, shape, value));
                return;
            case KindShape kind:
                if (LuaTables.RawString(state, value, Shapes.KindTag) != kind.Kind)
                    problems.Add(new ShapeProblem(DiagnosticCodes.InvalidValue, $"{Subject(path)} must be a {kind.TypeName} (from {kind.Origin}), got {Describe(value)}"));
                return;
            case UnionShape union:
                VisitUnion(value, union, path, problems);
                return;
            case ListShape list:
                VisitList(value, list, path, problems);
                return;
            case MapShape map:
                VisitMap(value, map, path, problems);
                return;
            case ObjectShape objectShape:
                VisitObject(value, objectShape, path, problems);
                return;
        }
    }

    void VisitUnion(LuaValue value, UnionShape union, string path, List<ShapeProblem> problems)
    {
        var attempts = union.Options.Select(option => (option, problems: Check(value, option, path))).ToList();
        if (attempts.Any(attempt => attempt.problems.Count == 0))
            return;

        var structural = attempts.Where(attempt => attempt.option is ObjectShape && IsPlainTable(value)).ToList();
        if (structural.Count == 1)
            problems.AddRange(structural[0].problems);
        else
            problems.Add(Invalid(path, union, value));
    }

    void VisitList(LuaValue value, ListShape list, string path, List<ShapeProblem> problems)
    {
        if (!IsPlainTable(value))
        {
            problems.Add(Invalid(path, list, value));
            return;
        }

        var pairs = LuaTables.Pairs(value.Read<LuaTable>());
        if (pairs.Count == 0)
            return;
        if (!LuaTables.IsList(pairs))
        {
            problems.Add(new ShapeProblem(DiagnosticCodes.InvalidValue, $"{Subject(path)} must be a list {list.Name}"));
            return;
        }

        var items = LuaTables.ListItems(pairs);
        for (var index = 0; index < items.Count; index++)
            Visit(items[index], list.Item, $"{path}[{index + 1}]", problems);
    }

    void VisitMap(LuaValue value, MapShape map, string path, List<ShapeProblem> problems)
    {
        if (!IsPlainTable(value))
        {
            problems.Add(Invalid(path, map, value));
            return;
        }

        foreach (var (key, item) in LuaTables.Pairs(value.Read<LuaTable>()))
        {
            if (key.Type != LuaValueType.String)
            {
                problems.Add(new ShapeProblem(DiagnosticCodes.InvalidValue, $"{Subject(path)} must be a map with string keys {map.Name}"));
                return;
            }
            Visit(item, map.Value, Join(path, key.Read<string>()), problems);
        }
    }

    void VisitObject(LuaValue value, ObjectShape shape, string path, List<ShapeProblem> problems)
    {
        if (!IsPlainTable(value))
        {
            problems.Add(Invalid(path, shape, value));
            return;
        }

        var table = value.Read<LuaTable>();
        foreach (var (key, _) in LuaTables.Pairs(table))
        {
            if (key.Type != LuaValueType.String)
                problems.Add(new ShapeProblem(DiagnosticCodes.InvalidValue, $"{Subject(path)} has a non-string key {LuaTables.KeyText(key)}; {shape.Name} takes named options"));
            else if (shape.Find(key.Read<string>()) is null)
                problems.Add(new ShapeProblem(DiagnosticCodes.UnknownOption, $"unknown option '{Join(path, key.Read<string>())}' ({shape.Name} takes {string.Join(", ", shape.Fields.Select(field => field.Name))})"));
        }

        foreach (var field in shape.Fields)
        {
            var fieldValue = LuaTables.Raw(state, table, field.Name);
            if (fieldValue.IsNil)
            {
                if (field.Required)
                    problems.Add(new ShapeProblem(DiagnosticCodes.MissingOption, $"missing required option '{Join(path, field.Name)}'"));
                continue;
            }
            Visit(fieldValue, field.Shape, Join(path, field.Name), problems);
        }
    }

    bool IsPlainTable(LuaValue value) =>
        value.Type == LuaValueType.Table && Tags.All(tag => LuaTables.Raw(state, value, tag).IsNil);

    ShapeProblem Invalid(string path, Shape shape, LuaValue value) =>
        new(DiagnosticCodes.InvalidValue, $"{Subject(path)} must be {shape.Name}, got {Describe(value)}");

    string Describe(LuaValue value)
    {
        if (value.Type != LuaValueType.Table)
            return value.Type switch
            {
                LuaValueType.String => $"string \"{value.Read<string>()}\"",
                LuaValueType.Number => $"number {LuaTables.KeyText(value)}",
                LuaValueType.Boolean => value.Read<bool>() ? "true" : "false",
                _ => value.Type.ToString().ToLowerInvariant(),
            };

        if (!LuaTables.Raw(state, value, Shapes.SecretTag).IsNil)
            return "a Secret";
        if (!LuaTables.Raw(state, value, Shapes.OutputTag).IsNil)
            return "an Output (known after apply)";
        if (!LuaTables.Raw(state, value, Shapes.FileTag).IsNil)
            return "a FileRef";
        return LuaTables.RawString(state, value, Shapes.KindTag) is { } kind ? $"a {kind}" : "a table";
    }

    static string Subject(string path) => path.Length == 0 ? "value" : $"'{path}'";

    static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
