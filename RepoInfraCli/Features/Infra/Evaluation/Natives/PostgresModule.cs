using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed class PostgresModule : INativeModule
{
    const string DefaultVersion = "17";

    static readonly ObjectShape DatabaseOptions = Object("DatabaseOptions",
        Required("version", Shapes.String),
        Required("password", Secret),
        Required("network", Network),
        Required("data", Shapes.String),
        Optional("user", Shapes.String));

    static readonly ObjectShape BackupOptions = Object("BackupOptions",
        Required("bucket", Bucket),
        Required("credentials", Secret),
        Required("schedule", Shapes.String),
        Optional("prefix", Shapes.String));

    public string Name => "postgres";

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("database", context.Named("postgres.database", (name, location) => context.Then(options => Database(context, name, options, location)))),
        KeyValuePair.Create("server", context.Function(args => Server(context, EvaluationContext.Argument(args, 0)))),
        KeyValuePair.Create("backup", context.Function(args =>
        {
            var location = context.Here();
            var database = EvaluationContext.Argument(args, 0);
            return context.Then(options => Backup(context, database, options, location));
        })),
    ]);

    static LuaValue Database(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        context.CheckOptions($"postgres.database \"{name}\"", options, DatabaseOptions, location);
        var id = NodeId.Of("postgres.database", name);
        var map = context.Converter.ConvertMap(options);
        context.Recorder.AddComponent(new ComponentNode(id, "postgres", NodeKinds.Database, name, map, false, location));
        context.Recorder.Databases[id] = new DatabaseInfo(
            id, name, map.GetString("user") ?? name, map.GetString("version"), context.Raw(options, "password"), context.Raw(options, "network"),
            map.GetString("data") ?? "");
        return DatabaseValues.Table(context, id, name, context.Output(id, "host"));
    }

    static LuaValue Server(EvaluationContext context, LuaValue database)
    {
        var location = context.Here();
        if (!context.CheckValue("postgres.server", database, Shapes.Database, location))
            return Placeholder(context);
        if (Info(context, database) is not { SelfHosted: true } info)
        {
            context.Error(DiagnosticCodes.InvalidValue, "postgres.server needs a database from postgres.database; managed and external databases run elsewhere", location);
            return Placeholder(context);
        }

        return context.Map(
        [
            KeyValuePair.Create("image", EvaluationContext.Text($"postgres:{info.Version ?? DefaultVersion}")),
            KeyValuePair.Create("restart", EvaluationContext.Text("unless-stopped")),
            KeyValuePair.Create("networks", context.List([info.Network])),
            KeyValuePair.Create("volumes", context.List([EvaluationContext.Text($"{info.Data}:/var/lib/postgresql/data")])),
            KeyValuePair.Create("environment", context.Map(
            [
                KeyValuePair.Create("POSTGRES_DB", EvaluationContext.Text(info.Name)),
                KeyValuePair.Create("POSTGRES_USER", EvaluationContext.Text(info.User)),
                KeyValuePair.Create("POSTGRES_PASSWORD", info.Password),
            ])),
            KeyValuePair.Create("healthcheck", context.Map(
            [
                KeyValuePair.Create("test", context.List([EvaluationContext.Text("CMD-SHELL"), EvaluationContext.Text($"pg_isready -U {info.User} -d {info.Name}")])),
                KeyValuePair.Create("interval", EvaluationContext.Text("10s")),
            ])),
        ]);
    }

    static LuaValue Backup(EvaluationContext context, LuaValue database, LuaValue options, SourceLocation? location)
    {
        var valid = context.CheckValue("postgres.backup", database, Shapes.Database, location);
        valid &= context.CheckOptions("postgres.backup", options, BackupOptions, location);
        if (!valid)
            return Placeholder(context);

        var info = Info(context, database);
        var name = info?.Name ?? context.RawString(database, "name") ?? "";
        var bucket = context.Raw(options, "bucket");
        var service = new List<KeyValuePair<string, LuaValue>>
        {
            KeyValuePair.Create("image", EvaluationContext.Text($"postgres:{info?.Version ?? DefaultVersion}")),
            KeyValuePair.Create("restart", EvaluationContext.Text("unless-stopped")),
            KeyValuePair.Create("environment", context.Map(
            [
                KeyValuePair.Create("DATABASE_URL", context.Raw(database, "connection_string")),
                KeyValuePair.Create("BACKUP_SCHEDULE", context.Raw(options, "schedule")),
                KeyValuePair.Create("BACKUP_BUCKET", context.Raw(bucket, "name")),
                KeyValuePair.Create("BACKUP_ENDPOINT", context.Raw(bucket, "endpoint")),
                KeyValuePair.Create("BACKUP_PREFIX", context.RawString(options, "prefix") is { } prefix ? EvaluationContext.Text(prefix) : EvaluationContext.Text($"postgres/{name}")),
                KeyValuePair.Create("BACKUP_CREDENTIALS", context.Raw(options, "credentials")),
            ])),
        };
        if (info is { SelfHosted: true })
            service.Add(KeyValuePair.Create("networks", context.List([info.Network])));
        return context.Map(service);
    }

    static DatabaseInfo? Info(EvaluationContext context, LuaValue database) =>
        context.NodeOf(database) is { } id ? context.Recorder.Databases.GetValueOrDefault(id) : null;

    static LuaValue Placeholder(EvaluationContext context) =>
        context.Map([KeyValuePair.Create("image", EvaluationContext.Text($"postgres:{DefaultVersion}"))]);
}
