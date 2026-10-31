using System.Text.RegularExpressions;
using NuLua;
using NuLua.Luau;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Validation;

namespace RepoInfraCli.Features.Infra.Evaluation;

public static partial class InfraEvaluator
{
    static readonly ObjectShape ExportsShape = Shapes.Object("Exports",
        Shapes.Optional("hosts", Shapes.Map(Shapes.Host)),
        Shapes.Optional("roles", Shapes.Map(Shapes.Role)),
        Shapes.Optional("networks", Shapes.Map(Shapes.Network)),
        Shapes.Optional("databases", Shapes.Map(Shapes.Database)));

    public static EvaluationResult Evaluate(InfraWorkspace workspace, EvaluationOptions options) =>
        Evaluate(workspace, options, NativeModuleRegistry.Default);

    public static EvaluationResult Evaluate(InfraWorkspace workspace, EvaluationOptions options, NativeModuleRegistry natives)
    {
        if (EnvRules.Check(workspace, options.Env) is { } envProblem)
            return new EvaluationResult(null, [envProblem]);
        if (!workspace.IsInitialized)
            return new EvaluationResult(null, [InfraDiagnostic.Error(DiagnosticCodes.ScriptError, $"No {RepoLocator.InfraFolderName}/{InfraWorkspace.EntryFileName} in {workspace.Root}")]);

        using var state = LuauState.Create();
        OpenSandbox(state);
        var context = new EvaluationContext(workspace, options, state);
        var loader = new InfraModuleLoader(context, natives);
        state.UseModuleLoader(loader);
        InstallPrint(context);

        InfraGraph? graph = null;
        try
        {
            var exports = RunEntry(context, loader);
            RecordExports(context, exports);
            if (!context.IsReference)
                ExpandPlays(context);
            graph = Finish(context);
        }
        catch (InfraScriptException exception)
        {
            context.Diagnostics.Add(exception.ToDiagnostic());
        }
        catch (LuaException exception)
        {
            context.Diagnostics.Add(ScriptDiagnostic(context, exception));
        }

        options.Log?.Debug("evaluate.done", $"Evaluated {workspace.RelativePath(workspace.EntryFile)}", new Dictionary<string, object?>
        {
            ["mode"] = options.Mode.ToString(),
            ["env"] = options.Env,
            ["resources"] = graph?.Resources.Count,
            ["diagnostics"] = context.Diagnostics.Count,
        });

        var diagnostics = graph is null ? context.Diagnostics : [.. context.Diagnostics, .. GraphValidator.Validate(graph)];
        return new EvaluationResult(graph, diagnostics);
    }

    static void OpenSandbox(LuauState state)
    {
        state.OpenBaseLibrary();
        state.OpenTableLibrary();
        state.OpenStringLibrary();
        state.OpenMathLibrary();
        state.OpenUtf8Library();
        state.OpenBit32Library();
        state.OpenBufferLibrary();
        foreach (var name in (string[])["getfenv", "setfenv", "loadstring", "collectgarbage"])
        {
            state.PushNil();
            state.SetGlobal(name);
        }
    }

    static void InstallPrint(EvaluationContext context)
    {
        context.State.Push(context.Procedure(args =>
        {
            var parts = Enumerable.Range(0, args.Length).Select(index => context.Converter.Convert(args[index])?.Display() ?? args[index].Type.ToString().ToLowerInvariant());
            context.Options.Log?.Info("script.print", string.Join('\t', parts));
        }));
        context.State.SetGlobal("print");
    }

    static LuaValue RunEntry(EvaluationContext context, InfraModuleLoader loader)
    {
        var entry = context.Workspace.EntryFile;
        var code = File.ReadAllText(entry);
        context.RecordSource(entry, code);
        var top = context.State.GetTop();
        loader.Enter(entry);
        try
        {
            EvaluationContext.Load(context.State, code, entry);
            context.State.Call(0, 1);
            return context.State.ToLuaValue(-1);
        }
        finally
        {
            context.State.SetTop(top);
            loader.Exit();
        }
    }

    static void RecordExports(EvaluationContext context, LuaValue exports)
    {
        if (exports.IsNil)
            return;

        var location = ReturnLocation(context);
        if (!context.CheckOptions("returned Exports", exports, ExportsShape, location))
            return;

        context.Recorder.Exports = new ExportsNode(
            Nodes(context, context.Raw(exports, "hosts")),
            [.. context.Entries(context.Raw(exports, "roles")).Select(entry => entry.Key).Order(StringComparer.Ordinal)],
            Nodes(context, context.Raw(exports, "networks")),
            Nodes(context, context.Raw(exports, "databases")),
            location);
    }

    static Dictionary<string, NodeId> Nodes(EvaluationContext context, LuaValue map) =>
        context.Entries(map)
            .Select(entry => (entry.Key, Node: context.NodeOf(entry.Value)))
            .Where(entry => entry.Node is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Node!.Value, StringComparer.Ordinal);

    static SourceLocation ReturnLocation(EvaluationContext context)
    {
        var lines = File.ReadAllLines(context.Workspace.EntryFile);
        var line = Array.FindLastIndex(lines, text => ReturnStatement().IsMatch(text));
        return new SourceLocation(context.Workspace.RelativePath(context.Workspace.EntryFile), line < 0 ? lines.Length : line + 1);
    }

    static void ExpandPlays(EvaluationContext context)
    {
        var recorder = context.Recorder;
        foreach (var play in recorder.Plays)
        {
            var runs = new List<RoleRun>();
            foreach (var (host, table) in play.Hosts)
            {
                for (var index = 0; index < play.Roles.Count; index++)
                    runs.Add(RunRole(context, host, table, play.Roles[index], index));

                foreach (var appId in play.Apps)
                {
                    if (recorder.Apps.FirstOrDefault(app => app.Id == appId) is { Deploy: { } deploy } app)
                        runs.Add(Run(context, host, RoleRun.AppPrefix + app.Name, () => deploy.Invoke([table, context.Output(app.Build.Id, "image")])));
                }
            }
            recorder.Runs[play.Id] = runs;
        }
    }

    static RoleRun RunRole(EvaluationContext context, NodeId host, LuaValue table, LuaValue role, int index)
    {
        if (role.Type != LuaValueType.Function)
            return new RoleRun(host, context.RawString(role, Shapes.RoleTag) ?? $"role{index + 1}", true, []);

        var run = Run(context, host, null, () => role.Read<LuaFunction>().Invoke([table]));
        return run.Role.Length == 0 ? run with { Role = $"role{index + 1}" } : run;
    }

    static RoleRun Run(EvaluationContext context, NodeId host, string? role, Action invoke)
    {
        var recorder = new RunRecorder(host, role);
        context.Recorder.CurrentRun = recorder;
        try
        {
            invoke();
        }
        finally
        {
            context.Recorder.CurrentRun = null;
        }
        return new RoleRun(host, recorder.Role ?? "", false, [.. recorder.Tasks]);
    }

    static InfraGraph? Finish(EvaluationContext context)
    {
        var projects = context.Recorder.Projects;
        if (projects.Count == 0)
            context.Error(DiagnosticCodes.MissingProject,
                $"{InfraWorkspace.EntryFileName} must declare its project, as in infra.project \"name\" {{ }}",
                new SourceLocation(context.Workspace.RelativePath(context.Workspace.EntryFile), 1));
        foreach (var duplicate in projects.Skip(1))
            context.Error(DiagnosticCodes.DuplicateProject, $"infra.project \"{duplicate.Name}\" is declared again; a file declares exactly one project", duplicate.Location);

        return context.Recorder.Build(context.Options.Env);
    }

    static InfraDiagnostic ScriptDiagnostic(EvaluationContext context, LuaException exception)
    {
        var first = exception.Message.Split('\n')[0].Trim();
        return ErrorPosition().Match(first) is { Success: true } match
            ? InfraDiagnostic.Error(DiagnosticCodes.ScriptError, match.Groups["message"].Value,
                context.Location(match.Groups["file"].Value, int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            : InfraDiagnostic.Error(DiagnosticCodes.ScriptError, first, null);
    }

    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+): (?<message>.*)$")]
    private static partial Regex ErrorPosition();

    [GeneratedRegex(@"^\s*return\b")]
    private static partial Regex ReturnStatement();
}
