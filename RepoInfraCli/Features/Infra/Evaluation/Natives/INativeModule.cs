using NuLua;

namespace RepoInfraCli.Features.Infra.Evaluation.Natives;

public interface INativeModule
{
    string Name { get; }

    LuaValue Create(EvaluationContext context);
}

public sealed class NativeModuleRegistry(IEnumerable<INativeModule> modules)
{
    readonly Dictionary<string, INativeModule> byName = modules.ToDictionary(module => module.Name, StringComparer.Ordinal);

    public static NativeModuleRegistry Default { get; } = new([
        new InfraModule(),
        new TypesModule(),
        new DigitalOceanModule(),
        new CloudflareModule(),
        new ComposeModule(),
        new TraefikModule(),
        new PostgresModule(),
        new DotnetModule(),
        new RepoModule(),
    ]);

    public IEnumerable<string> Names => byName.Keys;

    public INativeModule? Find(string name) => byName.GetValueOrDefault(name);
}
