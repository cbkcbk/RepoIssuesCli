using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class HostTasks
{
    static readonly ObjectShape AptOptions = Object("AptOptions",
        Optional("update", Shapes.Boolean),
        Optional("upgrade", Literal("safe", "full", "dist")),
        Optional("packages", StringList),
        Optional("absent", StringList));

    static readonly ObjectShape AptRepoOptions = Object("AptRepoOptions",
        Required("url", Shapes.String),
        Required("key", Shapes.String),
        Optional("components", StringList));

    static readonly ObjectShape UserOptions = Object("UserOptions",
        Optional("shell", Shapes.String),
        Optional("groups", StringList),
        Optional("append", Shapes.Boolean),
        Optional("authorized_keys", List(Union(Shapes.File, Shapes.String))));

    static readonly ObjectShape FileOptions = Object("FileOptions",
        Optional("content", Shapes.String),
        Optional("source", Shapes.File),
        Optional("mode", Shapes.String),
        Optional("owner", Shapes.String),
        Optional("notify", Handler));

    static readonly ObjectShape UfwOptions = Object("UfwOptions",
        Optional("default", Literal("deny", "allow")),
        Optional("allow", List(FirewallRule)));

    static readonly ObjectShape ServiceOptions = Object("ServiceOptions",
        Optional("enabled", Shapes.Boolean),
        Optional("state", ServiceState));

    static readonly ObjectShape HandlerOptions = Object("HandlerOptions",
        Required("service", Shapes.String),
        Required("state", ServiceState));

    static readonly ObjectShape ComposeOptions = Object("ComposeOptions",
        Optional("dir", Shapes.String),
        Required("services", Map(ComposeService)),
        Optional("volumes", Map(Any)));

    static readonly ObjectShape ShellOptions = Object("ShellOptions", Required("command", Shapes.String));

    readonly EvaluationContext context;
    readonly KeyValuePair<string, LuaValue>[] methods;

    HostTasks(EvaluationContext context)
    {
        this.context = context;
        methods =
        [
            Direct(TaskKinds.Apt, AptOptions),
            WithName(TaskKinds.AptRepo, AptRepoOptions),
            WithName(TaskKinds.User, UserOptions),
            WithName(TaskKinds.File, FileOptions),
            Direct(TaskKinds.Ufw, UfwOptions),
            WithName(TaskKinds.Service, ServiceOptions),
            HandlerMethod(),
            WithName(TaskKinds.Compose, ComposeOptions),
            ShellMethod(),
        ];
    }

    public static HostTasks For(EvaluationContext context) => context.Service(created => new HostTasks(created));

    public LuaValue Table(NodeId id, string name) =>
        context.Node(id, NodeKinds.Host, [KeyValuePair.Create("name", EvaluationContext.Text(name)), .. methods]);

    KeyValuePair<string, LuaValue> Direct(string kind, ObjectShape shape) =>
        KeyValuePair.Create(kind, context.Procedure(args =>
        {
            var location = context.Here();
            Record(Self(kind, args), kind, null, EvaluationContext.Argument(args, 1), shape, location);
        }));

    KeyValuePair<string, LuaValue> WithName(string kind, ObjectShape shape) =>
        KeyValuePair.Create(kind, context.Function(args =>
        {
            var location = context.Here();
            var host = Self(kind, args);
            var name = context.RequireName($"h:{kind}", EvaluationContext.Argument(args, 1));
            return context.ThenNothing(options => Record(host, kind, name, options, shape, location));
        }));

    KeyValuePair<string, LuaValue> HandlerMethod() =>
        KeyValuePair.Create(TaskKinds.Handler, context.Function(args =>
        {
            var location = context.Here();
            var id = Record(Self(TaskKinds.Handler, args), TaskKinds.Handler, null, EvaluationContext.Argument(args, 1), HandlerOptions, location);
            return context.Node(id, NodeKinds.Handler, tags: [KeyValuePair.Create(HandlerTag, EvaluationContext.Text(id.Value))]);
        }));

    KeyValuePair<string, LuaValue> ShellMethod() =>
        KeyValuePair.Create(TaskKinds.Shell, context.Procedure(args =>
        {
            var location = context.Here();
            var host = Self(TaskKinds.Shell, args);
            var options = context.Map([KeyValuePair.Create("command", EvaluationContext.Argument(args, 1))]);
            Record(host, TaskKinds.Shell, null, options, ShellOptions, location);
        }));

    NodeId Self(string kind, LuaFuncArguments args)
    {
        var self = EvaluationContext.Argument(args, 0);
        if (context.KindOf(self) == NodeKinds.Host && context.NodeOf(self) is { } host)
            return host;
        throw context.Fail(DiagnosticCodes.InvalidValue, $"host tasks are methods: call h:{kind} with a colon on a Host");
    }

    NodeId Record(NodeId host, string kind, string? name, LuaValue options, ObjectShape shape, SourceLocation? location)
    {
        context.CheckOptions(name is null ? $"h:{kind}" : $"h:{kind} \"{name}\"", options, shape, location);
        var map = context.Converter.ConvertMap(options);
        var notifies = map["notify"] is NodeRef handler ? handler.Node : (NodeId?)null;
        var id = context.Recorder.NextTaskId(host, kind);

        if (context.Recorder.CurrentRun is not { } run)
        {
            context.Error(DiagnosticCodes.TaskOutsidePlay, $"h:{kind} was called outside a play; call host tasks from a role function or an app deploy that an infra.play runs", location);
            return id;
        }

        run.Role ??= context.OutermostFunctionName();
        run.Tasks.Add(new TaskNode(id, host, kind, name, map, location, notifies));
        return id;
    }
}
