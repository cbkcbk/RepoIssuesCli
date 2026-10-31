using System.Collections.Immutable;
using System.Text.RegularExpressions;
using NuLua;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Postgres;
using static RepoInfraCli.Features.Infra.Evaluation.Shapes;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public sealed partial class PostgresModule : INativeModule
{
    const string DefaultVersion = "17";
    const string DefaultRegion = "us-east-1";

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

    static readonly ObjectShape CopyOptions = Object("CopyOptions",
        Optional("scrub", Shapes.String),
        Optional("migrate", Shapes.App));

    public string Name => "postgres";

    public LuaValue Create(EvaluationContext context) => context.Map(
    [
        KeyValuePair.Create("database", context.Named(PostgresServices.DatabaseKind, (name, location) => context.Then(options => Database(context, name, options, location)))),
        KeyValuePair.Create("server", context.Function(args => Server(context, EvaluationContext.Argument(args, 0)))),
        KeyValuePair.Create("backup", context.Function(args =>
        {
            var location = context.Here();
            var database = EvaluationContext.Argument(args, 0);
            return context.Then(options => Backup(context, database, options, location));
        })),
        KeyValuePair.Create("copy", context.Function(args =>
        {
            var location = context.Here();
            var from = EvaluationContext.Argument(args, 0);
            var to = EvaluationContext.Argument(args, 1);
            return context.ThenNothing(options => Copy(context, from, to, options, location));
        })),
    ]);

    static void Copy(EvaluationContext context, LuaValue from, LuaValue to, LuaValue options, SourceLocation? location)
    {
        var valid = context.CheckValue("postgres.copy from", from, Shapes.Database, location);
        valid &= context.CheckValue("postgres.copy to", to, Shapes.Database, location);
        valid &= context.CheckOptions("postgres.copy", options, CopyOptions, location);
        if (!valid || context.IsReference)
            return;

        var subject = $"postgres.copy \"{context.RawString(from, "name")}\" to \"{context.RawString(to, "name")}\"";
        if (Info(context, from) is not { } source || Info(context, to) is not { } target)
        {
            context.Error(DiagnosticCodes.InvalidValue, $"{subject}: both databases must be declared in this repo; databases from repo.provider are copied where they are declared", location);
            return;
        }
        if (source.Id == target.Id)
            context.Error(DiagnosticCodes.InvalidValue, $"{subject}: copies a database onto itself", location);
        if (PostgresVersions.Major(source.Version) is not { } sourceMajor)
            context.Error(DiagnosticCodes.InvalidValue,
                $"{subject}: the postgres version of \"{source.Name}\" is unknown (existing clusters don't declare one); copy it with infra db copy instead", location);
        else if (PostgresVersions.Major(target.Version) is { } targetMajor && targetMajor < sourceMajor)
            context.Error(DiagnosticCodes.InvalidValue,
                $"{subject}: a postgres {sourceMajor} dump can't be restored into \"{target.Name}\", which runs postgres {targetMajor}", location);

        var map = context.Converter.ConvertMap(options);
        var scrub = map.GetString("scrub");
        if (scrub is not null && !ScrubExists(context.Workspace, scrub))
            context.Error(DiagnosticCodes.InvalidValue, $"{subject}: scrub file \"{scrub}\" does not exist in {RepoLocator.InfraFolderName}/", location);

        var key = context.Recorder.UniqueSuffix($"{source.Name}->{target.Name}",
            candidate => context.Recorder.Copies.Any(copy => copy.Id == NodeId.Of(CopyNode.Kind, candidate)));
        var migrate = map["migrate"] is NodeRef app ? app.Node : (NodeId?)null;
        context.Recorder.Copies.Add(new CopyNode(NodeId.Of(CopyNode.Kind, key), source.Id, target.Id, scrub, migrate, location));
    }

    static bool ScrubExists(InfraWorkspace workspace, string scrub)
    {
        var full = Path.GetFullPath(scrub, workspace.Folder);
        return InfraWorkspace.IsInside(workspace.Folder, full) && System.IO.File.Exists(full);
    }

    static LuaValue Database(EvaluationContext context, string name, LuaValue options, SourceLocation? location)
    {
        context.CheckOptions($"postgres.database \"{name}\"", options, DatabaseOptions, location);
        var id = PostgresServices.DatabaseId(name);
        var map = context.Converter.ConvertMap(options);
        var user = map.GetString("user") ?? name;
        foreach (var (what, value) in (ReadOnlySpan<(string, string)>)[("name", name), ("user", user)])
        {
            if (!SafeName().IsMatch(value))
                context.Error(DiagnosticCodes.InvalidName, $"postgres.database \"{name}\": {what} \"{value}\" must start with a letter or _ and use only letters, digits, _ and -", location);
        }

        context.Recorder.AddComponent(new ComponentNode(id, "postgres", NodeKinds.Database, name, map, false, location));
        context.Recorder.Databases[id] = new DatabaseInfo(
            id, name, user, map.GetString("version"), context.Raw(options, "password"), context.Raw(options, "network"), map.GetString("data") ?? "");
        return DatabaseValues.Table(context, id, name, context.Output(id, SelfHostedDatabases.HostField));
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

        var version = info.Version ?? DefaultVersion;
        return context.Map(
        [
            KeyValuePair.Create("image", EvaluationContext.Text($"postgres:{version}")),
            KeyValuePair.Create("restart", EvaluationContext.Text("unless-stopped")),
            KeyValuePair.Create("networks", context.List([info.Network])),
            KeyValuePair.Create("volumes", context.List([EvaluationContext.Text($"{info.Data}:{DataDirectory(version)}")])),
            KeyValuePair.Create("environment", context.Map(
            [
                KeyValuePair.Create("POSTGRES_DB", EvaluationContext.Text(info.Name)),
                KeyValuePair.Create("POSTGRES_USER", EvaluationContext.Text(info.User)),
                KeyValuePair.Create("POSTGRES_PASSWORD", info.Password),
            ])),
            KeyValuePair.Create("labels", context.Map([KeyValuePair.Create(PostgresServices.ServerLabel, EvaluationContext.Text(info.Name))])),
            KeyValuePair.Create("healthcheck", context.Map(
            [
                KeyValuePair.Create("test", context.List([EvaluationContext.Text("CMD-SHELL"), EvaluationContext.Text($"pg_isready -h 127.0.0.1 -U {info.User} -d {info.Name}")])),
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
        if (Info(context, database) is not { SelfHosted: true } info)
        {
            context.Error(DiagnosticCodes.InvalidValue, NotSelfHosted(context, database), location);
            return Placeholder(context);
        }

        var map = context.Converter.ConvertMap(options);
        var schedule = map.GetString("schedule") ?? "";
        if (CronSchedule.Problem(schedule) is { } problem)
            context.Error(DiagnosticCodes.InvalidValue, $"postgres.backup: {problem}", location);
        var prefix = (map.GetString("prefix") ?? $"postgres/{info.Name}").Trim('/');
        if (!SafePrefix().IsMatch(prefix) || prefix.Split('/').Any(segment => segment is "." or ".."))
            context.Error(DiagnosticCodes.InvalidValue, $"postgres.backup: prefix \"{prefix}\" must be path segments of letters, digits, '.', '_' and '-' separated by '/'", location);

        var bucket = context.Raw(options, "bucket");
        var bucketNode = context.NodeOf(bucket) is { } bucketId ? context.Recorder.Resources.FirstOrDefault(resource => resource.Id == bucketId) : null;
        if (bucketNode is not null && !Expires(bucketNode, prefix))
            context.Warning(DiagnosticCodes.InvalidValue,
                $"postgres.backup: no lifecycle rule on bucket \"{bucketNode.Name}\" covers \"{prefix}/\", so its dumps are never deleted; add {{ prefix = \"{prefix}/\", expire_days = 14 }} to the bucket's lifecycle",
                location);
        InstallScript(context, location);

        return context.Map(
        [
            KeyValuePair.Create("image", EvaluationContext.Text($"postgres:{info.Version ?? DefaultVersion}")),
            KeyValuePair.Create("restart", EvaluationContext.Text("unless-stopped")),
            KeyValuePair.Create("command", context.List([EvaluationContext.Text(BackupScript.ScheduleCommand)])),
            KeyValuePair.Create("networks", context.List([info.Network])),
            KeyValuePair.Create("volumes", context.List(BackupScript.Mounts.Select(EvaluationContext.Text))),
            KeyValuePair.Create("environment", context.Map(
            [
                KeyValuePair.Create("PGHOST", context.Raw(database, SelfHostedDatabases.HostField)),
                KeyValuePair.Create("PGUSER", EvaluationContext.Text(info.User)),
                KeyValuePair.Create("PGPASSWORD", info.Password),
                KeyValuePair.Create("PGDATABASE", EvaluationContext.Text(info.Name)),
                KeyValuePair.Create(BackupScript.Endpoint, context.Raw(bucket, "endpoint")),
                KeyValuePair.Create(BackupScript.Region, EvaluationContext.Text(bucketNode?.Options.GetString("region") ?? DefaultRegion)),
                KeyValuePair.Create(BackupScript.Bucket, context.Raw(bucket, "name")),
                KeyValuePair.Create(BackupScript.Prefix, EvaluationContext.Text(prefix)),
                KeyValuePair.Create(BackupScript.Schedule, EvaluationContext.Text(schedule)),
                KeyValuePair.Create(BackupScript.Credentials, context.Raw(options, "credentials")),
            ])),
            KeyValuePair.Create("labels", context.Map([KeyValuePair.Create(PostgresServices.BackupLabel, EvaluationContext.Text(info.Name))])),
            KeyValuePair.Create("healthcheck", context.Map(
            [
                KeyValuePair.Create("test", context.List([EvaluationContext.Text("CMD"), EvaluationContext.Text("test"), EvaluationContext.Text("-f"), EvaluationContext.Text(BackupScript.ReadyFile)])),
                KeyValuePair.Create("interval", EvaluationContext.Text("10s")),
            ])),
        ]);
    }

    static string NotSelfHosted(EvaluationContext context, LuaValue database)
    {
        var name = context.RawString(database, "name") ?? "";
        const string scope = "postgres.backup only backs up self-hosted databases (postgres.database run by postgres.server)";
        return Info(context, database) is null
            ? $"postgres.backup: \"{name}\" is declared in another repo; {scope}, so back it up where it is declared"
            : $"postgres.backup: \"{name}\" is a managed DigitalOcean database, which DigitalOcean backs up itself; {scope}";
    }

    static bool Expires(ResourceNode bucket, string prefix) =>
        bucket.Options["lifecycle"] is ListValue rules
        && rules.Items.OfType<MapValue>().Any(rule => rule["expire_days"] is NumberValue && rule.GetString("prefix") is { } covered
                                                      && (prefix + "/").StartsWith(covered, StringComparison.Ordinal));

    static void InstallScript(EvaluationContext context, SourceLocation? location)
    {
        if (context.Recorder.CurrentRun is not { } run)
        {
            if (!context.IsReference)
                context.Error(DiagnosticCodes.TaskOutsidePlay,
                    "postgres.backup installs its script on the host that runs it; call it inside the role or app deploy whose h:compose uses it", location);
            return;
        }
        if (run.Tasks.Any(task => task.Kind == TaskKinds.File && task.Name == BackupScript.HostPath))
            return;

        var options = new MapValue(ImmutableSortedDictionary.CreateRange<string, InfraValue>(StringComparer.Ordinal,
        [
            KeyValuePair.Create<string, InfraValue>("content", new StringValue(BackupScript.Content)),
            KeyValuePair.Create<string, InfraValue>("mode", new StringValue("0755")),
            KeyValuePair.Create<string, InfraValue>("owner", new StringValue("root:root")),
        ]));
        run.Role ??= context.OutermostFunctionName();
        run.Tasks.Add(new TaskNode(context.Recorder.NextTaskId(run.Host, TaskKinds.File), run.Host, TaskKinds.File, BackupScript.HostPath, options, location, null));
    }

    static string DataDirectory(string version) =>
        int.TryParse(string.Concat(version.TakeWhile(char.IsAsciiDigit)), out var major) && major >= 18 ? "/var/lib/postgresql" : "/var/lib/postgresql/data";

    static DatabaseInfo? Info(EvaluationContext context, LuaValue database) =>
        context.NodeOf(database) is { } id ? context.Recorder.Databases.GetValueOrDefault(id) : null;

    static LuaValue Placeholder(EvaluationContext context) =>
        context.Map([KeyValuePair.Create("image", EvaluationContext.Text($"postgres:{DefaultVersion}"))]);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex SafeName();

    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex SafePrefix();
}
