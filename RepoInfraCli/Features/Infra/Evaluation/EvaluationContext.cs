using System.Text.RegularExpressions;
using NuLua;
using NuLua.Luau;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed partial class EvaluationContext
{
    readonly Dictionary<string, string[]> sources = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Type, object> services = [];
    readonly Lazy<LuaTable> nodeMeta;
    readonly Lazy<LuaTable> outputMeta;
    readonly Lazy<LuaTable> secretMeta;

    public EvaluationContext(InfraWorkspace workspace, EvaluationOptions options, LuauState state)
    {
        Workspace = workspace;
        Options = options;
        State = state;
        Shapes = new ShapeChecker(state);
        Converter = new LuaValueConverter(state);
        nodeMeta = new(CreateNodeMeta);
        outputMeta = new(CreateOutputMeta);
        secretMeta = new(CreateSecretMeta);
    }

    public InfraWorkspace Workspace { get; }

    public EvaluationOptions Options { get; }

    public LuauState State { get; }

    public ShapeChecker Shapes { get; }

    public LuaValueConverter Converter { get; }

    public GraphRecorder Recorder { get; } = new();

    public List<InfraDiagnostic> Diagnostics { get; } = [];

    public bool IsReference => Options.Mode == EvaluationMode.Reference;

    public T Service<T>(Func<EvaluationContext, T> create) where T : class
    {
        if (services.TryGetValue(typeof(T), out var existing))
            return (T)existing;
        var created = create(this);
        services[typeof(T)] = created;
        return created;
    }

    public void Error(string code, string message, SourceLocation? location) => Diagnostics.Add(InfraDiagnostic.Error(code, message, location));

    public void Warning(string code, string message, SourceLocation? location) => Diagnostics.Add(InfraDiagnostic.Warning(code, message, location));

    public InfraScriptException Fail(string code, string message) => new(code, message, Here());

    public static string ChunkName(string fullPath) => "@" + fullPath.Replace('\\', '/');

    public static void Load(ILuaState state, string code, string fullPath)
    {
        state.LoadString(code, ChunkName(fullPath));
        if (state.GetType(-1) == LuaValueType.Function)
            return;

        var message = state.ToString(-1);
        state.Pop(1);
        throw new LuaException(SyntaxErrorCode, message);
    }

    const uint SyntaxErrorCode = 3;

    public void RecordSource(string fullPath, string code) => sources[Path.GetFullPath(fullPath)] = code.ReplaceLineEndings("\n").Split('\n');

    public SourceLocation? Here()
    {
        var depth = State.Debug.GetStackDepth();
        for (var level = 0; level <= depth; level++)
        {
            if (Frame(level) is { CurrentLine: > 0 } frame)
                return Location(frame.Source, frame.CurrentLine);
        }
        return null;
    }

    public SourceLocation Location(string chunkSource, int line)
    {
        var path = chunkSource.TrimStart('@');
        if (path.StartsWith("...", StringComparison.Ordinal))
            path = sources.Keys.FirstOrDefault(known => known.Replace('\\', '/').EndsWith(path[3..], StringComparison.OrdinalIgnoreCase)) ?? path[3..];
        return new SourceLocation(Workspace.RelativePath(Path.GetFullPath(path, Workspace.Root)), line);
    }

    public string? OutermostFunctionName()
    {
        for (var level = State.Debug.GetStackDepth(); level >= 0; level--)
        {
            if (Frame(level) is not { LineDefined: > 0 } frame)
                continue;
            return sources.TryGetValue(Path.GetFullPath(frame.Source.TrimStart('@')), out var lines) && frame.LineDefined <= lines.Length
                ? FunctionNameIn(lines[frame.LineDefined - 1])
                : null;
        }
        return null;
    }

    ScriptFrame? Frame(int level) =>
        State.Debug.TryGetStackInfo(level, LuaDebugInfoFields.Source | LuaDebugInfoFields.CurrentLine, out var info) && info.Source is ['@', ..] source
            ? new ScriptFrame(source, info.CurrentLine, info.LineDefined)
            : null;

    sealed record ScriptFrame(string Source, int CurrentLine, int LineDefined);

    static string? FunctionNameIn(string line)
    {
        if (NamedFunction().Match(line) is { Success: true } named)
            return named.Groups["name"].Value;
        return AssignedFunction().Match(line) is { Success: true } assigned ? assigned.Groups["name"].Value : null;
    }

    [GeneratedRegex(@"function\s+(?:[A-Za-z_]\w*[.:])*(?<name>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex NamedFunction();

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)\s*=\s*function\b")]
    private static partial Regex AssignedFunction();

    public LuaValue Function(Func<LuaFuncArguments, LuaValue> body) =>
        LuaValue.FromFunction(State.CreateFunction((state, args) =>
        {
            Push(state, body(args));
            return 1;
        }));

    public LuaValue Procedure(Action<LuaFuncArguments> body) =>
        LuaValue.FromFunction(State.CreateFunction((_, args) =>
        {
            body(args);
            return 0;
        }));

    public LuaValue Named(string api, Func<string, SourceLocation?, LuaValue> body) =>
        Function(args =>
        {
            var location = Here();
            return body(RequireName(api, Argument(args, 0)), location);
        });

    public LuaValue Then(Func<LuaValue, LuaValue> body) => Function(args => body(Argument(args, 0)));

    public LuaValue ThenNothing(Action<LuaValue> body) => Procedure(args => body(Argument(args, 0)));

    public static LuaValue Argument(LuaFuncArguments args, int index) => index < args.Length ? args[index] : LuaValue.Nil;

    public string RequireName(string api, LuaValue value)
    {
        if (value.Type == LuaValueType.String && value.Read<string>().Trim().Length > 0)
            return value.Read<string>();
        throw Fail(DiagnosticCodes.InvalidValue, $"{api} expects a name string, as in {api} \"name\"; got {value.Type.ToString().ToLowerInvariant()}");
    }

    public bool CheckOptions(string subject, LuaValue options, ObjectShape shape, SourceLocation? location)
    {
        if (options.IsNil && shape.Fields.All(field => !field.Required))
            return true;
        if (options.IsNil)
        {
            Error(DiagnosticCodes.MissingOption, $"{subject}: expects an options table {{ ... }}", location);
            return false;
        }

        var problems = Shapes.Check(options, shape);
        foreach (var problem in problems)
            Error(problem.Code, $"{subject}: {problem.Message}", location);
        return problems.Count == 0;
    }

    public bool CheckValue(string subject, LuaValue value, Shape shape, SourceLocation? location)
    {
        var problems = Shapes.Check(value, shape);
        foreach (var problem in problems)
            Error(problem.Code, $"{subject}: {problem.Message}", location);
        return problems.Count == 0;
    }

    public LuaTable Table() => State.CreateTable();

    public static LuaValue Text(string value) => LuaValue.FromString(value);

    public static void Push(ILuaState state, LuaValue value)
    {
        if (value.Type == LuaValueType.String && value.Read<string>().Length == 0)
            state.Concat(0);
        else
            state.Push(value);
    }

    public void Set(LuaTable table, LuaValue key, LuaValue value)
    {
        var top = State.GetTop();
        try
        {
            State.Push(LuaValue.FromTable(table));
            Push(State, key);
            Push(State, value);
            State.RawSet(-3);
        }
        finally
        {
            State.SetTop(top);
        }
    }

    public void Set(LuaTable table, string key, LuaValue value) => Set(table, Text(key), value);

    public LuaValue List(IEnumerable<LuaValue> items)
    {
        var table = Table();
        var index = 1;
        foreach (var item in items)
            Set(table, LuaValue.FromNumber(index++), item);
        return LuaValue.FromTable(table);
    }

    public LuaValue Map(IEnumerable<KeyValuePair<string, LuaValue>> entries)
    {
        var table = Table();
        foreach (var (key, value) in entries)
            Set(table, key, value);
        return LuaValue.FromTable(table);
    }

    public LuaValue Node(NodeId id, string kind, IEnumerable<KeyValuePair<string, LuaValue>>? fields = null, IEnumerable<KeyValuePair<string, LuaValue>>? tags = null)
    {
        var table = Table();
        Set(table, Evaluation.Shapes.NodeTag, Text(id.Value));
        Set(table, Evaluation.Shapes.KindTag, Text(kind));
        foreach (var (key, value) in (fields ?? []).Concat(tags ?? []))
            Set(table, key, value);
        return Seal(table, nodeMeta.Value);
    }

    public LuaValue Output(NodeId node, string field, bool sensitive = false)
    {
        var table = Table();
        Set(table, Evaluation.Shapes.OutputTag, Text($"{node.Value}.{field}"));
        Set(table, Evaluation.Shapes.NodeTag, Text(node.Value));
        Set(table, LuaValueConverter.FieldTag, Text(field));
        if (sensitive)
            Set(table, LuaValueConverter.SensitiveTag, LuaValue.FromBoolean(true));
        return Seal(table, outputMeta.Value);
    }

    public LuaValue Secret(string path)
    {
        Recorder.AddSecret(path);
        var table = Table();
        Set(table, Evaluation.Shapes.SecretTag, Text(path));
        return Seal(table, secretMeta.Value);
    }

    public LuaValue Raw(LuaValue table, string key) => LuaTables.Raw(State, table, key);

    public string? RawString(LuaValue table, string key) => LuaTables.RawString(State, table, key);

    public NodeId? NodeOf(LuaValue value) => RawString(value, Evaluation.Shapes.NodeTag) is { } id ? new NodeId(id) : null;

    public string? KindOf(LuaValue value) => RawString(value, Evaluation.Shapes.KindTag);

    public IReadOnlyList<LuaValue> Items(LuaValue list) =>
        list.Type == LuaValueType.Table ? LuaTables.ListItems(LuaTables.Pairs(list.Read<LuaTable>())) : [];

    public IReadOnlyList<KeyValuePair<string, LuaValue>> Entries(LuaValue map) =>
        map.Type == LuaValueType.Table
            ? [.. LuaTables.Pairs(map.Read<LuaTable>()).Where(pair => pair.Key.Type == LuaValueType.String).Select(pair => KeyValuePair.Create(pair.Key.Read<string>(), pair.Value))]
            : [];

    LuaValue Seal(LuaTable table, LuaTable meta)
    {
        var value = LuaValue.FromTable(table);
        State.Push(value);
        State.SetMetatable(-1, meta);
        State.Pop(1);
        return value;
    }

    LuaTable CreateNodeMeta()
    {
        var meta = Table();
        Set(meta, "__index", Function(args =>
            throw Fail(DiagnosticCodes.InvalidValue, $"{Describe(Argument(args, 0))} has no field '{KeyName(Argument(args, 1))}'")));
        Set(meta, "__newindex", Procedure(args =>
            throw Fail(DiagnosticCodes.InvalidValue, $"{Describe(Argument(args, 0))} is read-only; can't set '{KeyName(Argument(args, 1))}'")));
        return meta;
    }

    LuaTable CreateOutputMeta()
    {
        var meta = Table();
        Set(meta, "__tostring", Function(args =>
            Text(Raw(Argument(args, 0), LuaValueConverter.SensitiveTag).IsNil ? InfraValue.UnknownDisplay : InfraValue.SecretDisplay)));
        foreach (var name in (string[])["__index", "__newindex", "__concat", "__add", "__sub", "__mul", "__div", "__idiv", "__mod", "__pow", "__unm", "__len", "__lt", "__le", "__call"])
            Set(meta, name, Function(args => throw OutputMisuse(args)));
        return meta;
    }

    LuaTable CreateSecretMeta()
    {
        var meta = Table();
        Set(meta, "__tostring", Function(_ => Text(InfraValue.SecretDisplay)));
        foreach (var name in (string[])["__index", "__newindex", "__concat", "__len", "__call"])
            Set(meta, name, Function(args =>
                throw Fail(DiagnosticCodes.OutputMisuse, $"secret \"{SecretPath(args)}\" can't be read or combined during evaluation; pass the secret itself as an option value")));
        return meta;
    }

    InfraScriptException OutputMisuse(LuaFuncArguments args)
    {
        var output = Enumerable.Range(0, args.Length).Select(index => RawString(args[index], Evaluation.Shapes.OutputTag)).FirstOrDefault(text => text is not null);
        return Fail(DiagnosticCodes.OutputMisuse,
            $"{output ?? "this value"} is a live value {InfraValue.UnknownDisplay} and can't be used during evaluation (no string concatenation, field access or arithmetic); pass it directly as an option value instead");
    }

    string SecretPath(LuaFuncArguments args) =>
        Enumerable.Range(0, args.Length).Select(index => RawString(args[index], Evaluation.Shapes.SecretTag)).FirstOrDefault(text => text is not null) ?? "";

    string Describe(LuaValue value)
    {
        var name = RawString(value, "name");
        var kind = KindOf(value) ?? "value";
        return name is null ? $"{kind} {NodeOf(value)}" : $"{kind} \"{name}\"";
    }

    static string KeyName(LuaValue key) => key.Type == LuaValueType.String ? key.Read<string>() : LuaTables.KeyText(key);
}
