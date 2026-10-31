using System.Text.RegularExpressions;
using NuLua;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed partial class InfraModule : INativeModule
{
    static readonly ObjectShape ProjectOptions = Shapes.Object("ProjectOptions", Shapes.Optional("prune", Shapes.Boolean));

    static readonly ObjectShape HostOptions = Shapes.Object("HostOptions",
        Shapes.Required("address", Shapes.InputString),
        Shapes.Required("user", Shapes.String),
        Shapes.Optional("port", Shapes.Number),
        Shapes.Optional("become", Shapes.Boolean),
        Shapes.Optional("become_password", Shapes.Secret));

    static readonly ObjectShape AppOptions = Shapes.Object("AppOptions",
        Shapes.Required("build", Shapes.Build),
        Shapes.Required("deploy", Shapes.Function));

    static readonly ObjectShape PlayOptions = Shapes.Object("PlayOptions",
        Shapes.Required("hosts", Shapes.List(Shapes.Host)),
        Shapes.Optional("roles", Shapes.List(Shapes.Role)),
        Shapes.Optional("apps", Shapes.List(Shapes.App)));

    public string Name => "infra";

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("git", Git(context)),
        KeyValuePair.Create("env", context.Options.Env is { } env ? EvaluationContext.Text(env) : LuaValue.Nil),
        KeyValuePair.Create("secret", context.Function(args => Secret(context, EvaluationContext.Argument(args, 0)))),
        KeyValuePair.Create("file", context.Function(args => File(context, EvaluationContext.Argument(args, 0)))),
        KeyValuePair.Create("project", context.Named("infra.project", (name, location) => context.ThenNothing(options => Project(context, name, options, location)))),
        KeyValuePair.Create("host", context.Named("infra.host", (name, location) => context.Then(options => Host(context, name, options, location)))),
        KeyValuePair.Create("app", context.Named("infra.app", (name, location) => context.Then(options => App(context, name, options, location)))),
        KeyValuePair.Create("play", context.Named("infra.play", (name, location) => context.ThenNothing(options => Play(context, name, options, location)))),
    ]);

    static LuaValue Git(EvaluationContext context)
    {
        var root = context.Workspace.Root;
        return context.Map(
        [
            KeyValuePair.Create("sha", EvaluationContext.Text(GitValue(root, "rev-parse", "HEAD"))),
            KeyValuePair.Create("branch", EvaluationContext.Text(GitValue(root, "rev-parse", "--abbrev-ref", "HEAD"))),
        ]);
    }

    static string GitValue(string root, params string[] arguments) =>
        Directory.Exists(root) && RepoCli.Core.Features.Repo.Git.Run(root, arguments) is { Succeeded: true } result ? result.Output.Trim() : "";

    static LuaValue Secret(EvaluationContext context, LuaValue path)
    {
        var location = context.Here();
        var text = path.Type == LuaValueType.String ? path.Read<string>() : "";
        if (!SecretPath().IsMatch(text))
            context.Error(DiagnosticCodes.InvalidValue, $"infra.secret \"{text}\": secret paths are slash-separated names like \"digitalocean/token\"", location);
        return context.Secret(text);
    }

    static LuaValue File(EvaluationContext context, LuaValue path)
    {
        var location = context.Here();
        var text = path.Type == LuaValueType.String ? path.Read<string>() : "";
        var resolved = Resolve(context.Workspace.Root, text, out var inside);
        if (text.Length == 0)
            context.Error(DiagnosticCodes.InvalidValue, "infra.file expects a path string", location);
        else if (!inside)
            context.Error(DiagnosticCodes.FileOutsideRepo,
                $"infra.file \"{text}\" resolves outside the repository; use a path relative to the repo root or one under ~/", location);

        return context.Map(
        [
            KeyValuePair.Create(Shapes.FileTag, EvaluationContext.Text(text)),
            KeyValuePair.Create(LuaValueConverter.ResolvedField, EvaluationContext.Text(resolved)),
        ]);
    }

    public static string Resolve(string root, string path, out bool inside)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var underHome = Path.GetFullPath(Path.Combine(home, path.Length > 2 ? path[2..] : ""));
            inside = InfraWorkspace.IsInside(home, underHome);
            return underHome;
        }

        var full = Path.GetFullPath(Path.Combine(root, path));
        inside = InfraWorkspace.IsInside(root, full);
        return full;
    }

    static void Project(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        context.CheckOptions($"infra.project \"{name}\"", options, ProjectOptions, location);
        context.Recorder.Projects.Add(new ProjectNode(name, context.Converter.ConvertMap(options).GetBool("prune"), location));
    }

    static LuaValue Host(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        context.CheckOptions($"infra.host \"{name}\"", options, HostOptions, location);
        var map = context.Converter.ConvertMap(options);
        var id = NodeId.Of(NodeKinds.Host, name);
        context.Recorder.Hosts.Add(new HostNode(
            id, name, map["address"] ?? new StringValue(""), map.GetString("user") ?? "", Port(map), map.GetBool("become"), map["become_password"] as SecretRef,
            null, false, null, location));
        return HostTasks.For(context).Table(id, name);
    }

    public static int? Port(MapValue options) => options["port"] is NumberValue number ? (int)number.Value : null;

    static LuaValue App(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        var id = NodeId.Of(NodeKinds.App, name);
        if (context.CheckOptions($"infra.app \"{name}\"", options, AppOptions, location))
        {
            var buildId = context.NodeOf(context.Raw(options, "build"));
            var build = context.Recorder.Builds.First(candidate => candidate.Id == buildId);
            var deploy = context.Raw(options, "deploy").Read<LuaFunction>();
            context.Recorder.Apps.Add(new AppDraft(id, name, build, deploy, location));
        }

        return context.Node(id, NodeKinds.App, [KeyValuePair.Create("name", EvaluationContext.Text(name))]);
    }

    static void Play(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        context.CheckOptions($"infra.play \"{name}\"", options, PlayOptions, location);
        var hosts = context.Items(context.Raw(options, "hosts"))
            .Select(host => (Id: context.NodeOf(host), Table: host))
            .Where(host => host.Id is not null && context.KindOf(host.Table) == NodeKinds.Host)
            .Select(host => (host.Id!.Value, host.Table))
            .ToList();
        var roles = context.Items(context.Raw(options, "roles"))
            .Where(role => role.Type == LuaValueType.Function || context.KindOf(role) == NodeKinds.Role)
            .ToList();
        var apps = context.Items(context.Raw(options, "apps"))
            .Select(context.NodeOf)
            .OfType<NodeId>()
            .ToList();
        context.Recorder.Plays.Add(new PlayDraft(NodeId.Of("play", name), name, hosts, roles, apps, location));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$")]
    private static partial Regex SecretPath();
}