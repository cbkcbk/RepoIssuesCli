using System.Collections.Immutable;
using NuLua;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;
using RepoInfraCli.Features.Infra.Repos;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed class ImportedRepo : IDisposable
{
    static readonly ExportsNode NoExports = new(new Dictionary<string, NodeId>(), [], new Dictionary<string, NodeId>(), new Dictionary<string, NodeId>(), null);

    readonly EvaluationContext importer;
    readonly EvaluationSession session;
    readonly RepoCheckout checkout;
    int reported;

    ImportedRepo(EvaluationContext importer, EvaluationSession session, RepoCheckout checkout, string label, NodeId provider)
    {
        this.importer = importer;
        this.session = session;
        this.checkout = checkout;
        Label = label;
        Provider = provider;
    }

    public string Label { get; }

    public NodeId Provider { get; }

    public bool Resolved => session.Result.Graph is not null;

    public Marker Marker => session.Result.Graph?.Marker ?? new Marker("", null);

    EvaluationContext Source => session.Context;

    ExportsNode Exports => Source.Recorder.Exports ?? NoExports;

    public static ImportedRepo? Open(EvaluationContext importer, ProviderNode provider, string label, SourceLocation? location)
    {
        if (provider.Options.GetString("url") is not { } url || provider.Options.GetString("ref") is not { } reference)
            return null;

        var source = new RepoSource(url, reference, provider.Options.GetString("path"));
        var chain = importer.Options.RepoChain.Add(RepoSource.Key(url));
        if (importer.Options.RepoChain.Contains(RepoSource.Key(url)))
        {
            importer.Error(DiagnosticCodes.RepoCycle,
                $"repo.provider {label}: {string.Join(" -> ", chain)} is a cycle; a repo can't import itself through its providers", location);
            return null;
        }

        RepoCheckout checkout;
        try
        {
            checkout = importer.Options.Repos.Fetch(source);
        }
        catch (RepoFetchException exception)
        {
            importer.Warning(DiagnosticCodes.RepoUnavailable,
                $"repo.provider {label} ({source.Display}) is unavailable: {exception.Message.TrimEnd('.')}. Its hosts, roles, networks and databases stay unresolved, so plan and apply fail until it can be fetched",
                location);
            return null;
        }

        if (checkout.Offline)
            importer.Options.Log?.Info("repo.offline", $"repo.provider {label}: {source.Display} was not fetched; using the cached commit {checkout.Commit}");

        var workspace = new InfraWorkspace(checkout.Root, checkout.InfraFolder);
        var env = Directory.Exists(workspace.EnvsFolder) ? importer.Options.Env : null;
        if (InfraEvaluator.Precheck(workspace, env) is { } problem)
        {
            importer.Error(problem.Code, $"repo.provider {label} ({source.Display}): {problem.Message}", location);
            return null;
        }

        var options = importer.Options with { Env = env, Mode = EvaluationMode.Reference, RepoChain = chain };
        var imported = new ImportedRepo(importer, InfraEvaluator.Open(workspace, options, importer.Natives), checkout, label, provider.Id);
        importer.Imports.Add(imported);
        foreach (var diagnostic in imported.session.Result.Diagnostics)
            imported.Report(diagnostic);
        imported.reported = imported.Source.Diagnostics.Count;
        return imported.Resolved ? imported : null;
    }

    public LuaValue? Host(string name, SourceLocation? location)
    {
        if (Find("host", Exports.Hosts, name, location) is not { } id)
            return null;
        var host = ImportHost(id, location);
        return HostTasks.For(importer).Table(host.Id, host.Name);
    }

    public void CheckRole(string name, SourceLocation? location)
    {
        if (!Exports.Roles.Contains(name))
            MissingExport("role", name, Exports.Roles, location);
    }

    public LuaValue? Network(string name, SourceLocation? location)
    {
        if (Find("network", Exports.Networks, name, location) is not { } id)
            return null;
        var network = ImportComponent(id);
        return importer.Node(network.Id, NodeKinds.Network, [KeyValuePair.Create("name", EvaluationContext.Text(network.Name))]);
    }

    public LuaValue? Database(string name, SourceLocation? location)
    {
        if (Find("database", Exports.Databases, name, location) is not { } id)
            return null;
        if (Source.Recorder.Resources.FirstOrDefault(resource => resource.Id == id) is not { Parent: { } cluster } database)
        {
            importer.Error(DiagnosticCodes.InvalidValue,
                $"repo.provider {Label}: database \"{name}\" is a self-hosted postgres.database; only cluster databases can be imported, because a self-hosted server only exists in the plays of the repo that declares it",
                location);
            return null;
        }

        var imported = ImportResource(id);
        return DatabaseValues.Table(importer, imported, database.Name, importer.Output(Rebase(cluster), "host"));
    }

    public RoleRun Run(NodeId host, string hostName, string role)
    {
        var name = $"{Label}/{role}";
        if (!Resolved || !Exports.Roles.Contains(role))
            return new RoleRun(host, name, true, []);

        var run = new RoleRun(host, role, false, []);
        try
        {
            run = InfraEvaluator.RunExported(Source, host, hostName, Source.Raw(Source.Raw(session.Exports, "roles"), role), role);
        }
        catch (InfraScriptException exception)
        {
            Source.Diagnostics.Add(exception.ToDiagnostic());
        }
        catch (LuaException exception)
        {
            Source.Diagnostics.Add(InfraEvaluator.ScriptDiagnostic(Source, exception));
        }

        foreach (var diagnostic in Source.Diagnostics.Skip(reported))
            Report(diagnostic);
        reported = Source.Diagnostics.Count;
        return new RoleRun(host, name, run.External, [.. run.Tasks.Select(ImportTask)]);
    }

    NodeId? Find(string kind, IReadOnlyDictionary<string, NodeId> exported, string name, SourceLocation? location)
    {
        if (exported.TryGetValue(name, out var id))
            return id;
        MissingExport(kind, name, [.. exported.Keys.Order(StringComparer.Ordinal)], location);
        return null;
    }

    void MissingExport(string kind, string name, IReadOnlyList<string> exported, SourceLocation? location) =>
        importer.Error(DiagnosticCodes.MissingExport,
            $"repo.provider {Label} exports no {kind} \"{name}\"; its {kind}s: {(exported.Count == 0 ? "(none)" : string.Join(", ", exported))}", location);

    void Report(InfraDiagnostic diagnostic) =>
        importer.Diagnostics.Add(diagnostic with { Message = $"repo.provider {Label}: {diagnostic.Message}", Location = Relocate(diagnostic.Location) });

    SourceLocation? Relocate(SourceLocation? location) => location is null ? null : location with { File = $"{Label}/{location.File}" };

    NodeId Rebase(NodeId id) => new($"{Label}/{id.Value}");

    TaskNode ImportTask(TaskNode task) => task with
    {
        Id = Rebase(task.Id),
        Options = (MapValue)Rewrite(task.Options, Import, used: true),
        Location = Relocate(task.Location),
        Notifies = task.Notifies is { } handler ? Rebase(handler) : null,
    };

    NodeId Import(NodeId id)
    {
        if (Source.Recorder.Hosts.Any(host => host.Id == id))
            return ImportHost(id, null).Id;
        if (Source.Recorder.Resources.Any(resource => resource.Id == id))
            return ImportResource(id);
        if (Source.Recorder.Components.Any(component => component.Id == id))
            return ImportComponent(id).Id;
        return Rebase(id);
    }

    HostNode ImportHost(NodeId id, SourceLocation? location)
    {
        var target = Rebase(id);
        if (importer.Recorder.Hosts.FirstOrDefault(host => host.Id == target) is { } existing)
            return existing;

        var host = Source.Recorder.Hosts.First(node => node.Id == id);
        if (host.BecomePassword is { } password)
            importer.Recorder.AddSecret(password.Path);
        var imported = host with
        {
            Id = target,
            Address = Rewrite(host.Address, Import, used: true),
            Resource = host.Resource is { } resource ? ImportResource(resource) : null,
            Provider = Provider,
            Location = location ?? Relocate(host.Location),
        };
        importer.Recorder.Hosts.Add(imported);
        return imported;
    }

    NodeId ImportResource(NodeId id)
    {
        var target = Rebase(id);
        if (importer.Recorder.Resources.Any(resource => resource.Id == target))
            return target;

        var node = Source.Recorder.Resources.First(resource => resource.Id == id);
        var parent = node.Parent is { } parentId ? ImportResource(parentId) : (NodeId?)null;
        importer.Recorder.Resources.Add(node with
        {
            Id = target,
            ProviderId = ImportProvider(node.ProviderId),
            Parent = parent,
            Options = (MapValue)Rewrite(node.Options, Rebase, used: false),
            Protect = false,
            Exported = false,
            Location = Relocate(node.Location),
            DependsOn = parent is { } dependency ? [dependency] : [],
            Reference = node.Existing ? null : node.Reference ?? Marker,
        });
        return target;
    }

    NodeId ImportProvider(NodeId id)
    {
        var target = Rebase(id);
        if (importer.Recorder.Providers.All(provider => provider.Id != target))
        {
            var provider = Source.Recorder.Providers.First(node => node.Id == id);
            importer.Recorder.Providers.Add(provider with
            {
                Id = target,
                Options = (MapValue)Rewrite(provider.Options, Rebase, used: true),
                Location = Relocate(provider.Location),
                Reference = provider.Reference ?? Marker,
            });
        }

        return target;
    }

    ComponentNode ImportComponent(NodeId id)
    {
        var target = Rebase(id);
        if (importer.Recorder.Components.FirstOrDefault(component => component.Id == target) is { } existing)
            return existing;

        var component = Source.Recorder.Components.First(node => node.Id == id);
        var imported = component with { Id = target, Options = (MapValue)Rewrite(component.Options, Rebase, used: false), Location = Relocate(component.Location) };
        importer.Recorder.AddComponent(imported);
        return imported;
    }

    InfraValue Rewrite(InfraValue value, Func<NodeId, NodeId> ids, bool used) => value switch
    {
        OutputRef output => output with { Node = ids(output.Node) },
        NodeRef node => new NodeRef(ids(node.Node)),
        MapValue map => new MapValue(map.Entries.ToImmutableSortedDictionary(entry => entry.Key, entry => Rewrite(entry.Value, ids, used), StringComparer.Ordinal)),
        ListValue list => new ListValue([.. list.Items.Select(item => Rewrite(item, ids, used))]),
        SecretRef secret when used => Use(secret),
        FileRef file when used => Use(file),
        _ => value,
    };

    SecretRef Use(SecretRef secret)
    {
        importer.Recorder.AddSecret(secret.Path);
        return secret;
    }

    FileRef Use(FileRef file)
    {
        if (File.Exists(file.Resolved) || !InfraWorkspace.IsInside(checkout.Root, file.Resolved))
            return file;
        try
        {
            importer.Options.Repos.Materialize(checkout, Path.GetRelativePath(checkout.Root, file.Resolved));
        }
        catch (RepoFetchException exception)
        {
            importer.Error(DiagnosticCodes.RepoUnavailable, $"repo.provider {Label}: {file.Path} could not be fetched: {exception.Message}", null);
        }

        return file;
    }

    public void Dispose() => session.Dispose();
}