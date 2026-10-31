using NuLua;
using RepoInfraCli.Features.Infra.Evaluation.Natives;
using RepoInfraCli.Features.Infra.Graph;

namespace RepoInfraCli.Features.Infra.Evaluation;

public sealed class InfraModuleLoader(EvaluationContext context, NativeModuleRegistry natives) : LuaModuleLoader
{
    public const string NativeAlias = "infra";
    const string NativeRoot = "<native>/@infra";
    const string Extension = ".luau";

    readonly Stack<string> loading = new();

    public void Enter(string fullPath) => loading.Push(Path.GetFullPath(fullPath));

    public void Exit() => loading.Pop();

    protected override bool TryGetAliasPath(string alias, out string path)
    {
        if (alias == NativeAlias)
        {
            path = NativeRoot;
            return true;
        }

        throw context.Fail(DiagnosticCodes.RequireDenied,
            $"require(\"@{alias}\") is not allowed; only @infra modules and .luau files inside {RepoLocator.InfraFolderName}/ can be required");
    }

    protected override string GetCacheKey(string path) => IsNative(path) ? NativeName(path) : ResolveFile(path);

    protected override bool TryLoadModule(ILuaState state, string fullPath, string requireArgument)
    {
        if (IsNative(fullPath))
        {
            if (natives.Find(NativeName(fullPath)) is not { } module)
                throw context.Fail(DiagnosticCodes.ScriptError,
                    $"module '{requireArgument}' not found; @infra has {string.Join(", ", natives.Names.Order(StringComparer.Ordinal))}");
            state.Push(module.Create(context));
            return true;
        }

        var file = ResolveFile(fullPath);
        if (!File.Exists(file))
            throw context.Fail(DiagnosticCodes.ScriptError, $"module '{requireArgument}' not found at {context.Workspace.RelativePath(file)}");

        var code = File.ReadAllText(file);
        context.RecordSource(file, code);
        EvaluationContext.Load(state, code, file);
        Enter(file);
        try
        {
            state.Call(0, 1);
        }
        finally
        {
            Exit();
        }
        return true;
    }

    static bool IsNative(string path) => path.Replace('\\', '/').StartsWith(NativeRoot, StringComparison.Ordinal);

    static string NativeName(string path)
    {
        var name = path.Replace('\\', '/')[NativeRoot.Length..].Trim('/');
        return name is "" or "init" ? NativeAlias : name;
    }

    string ResolveFile(string path)
    {
        if (!path.StartsWith("./", StringComparison.Ordinal) && !path.StartsWith("../", StringComparison.Ordinal))
            throw context.Fail(DiagnosticCodes.RequireDenied,
                $"require(\"{path}\") is not allowed; use \"@infra/...\" or a relative path like \"./module\" inside {RepoLocator.InfraFolderName}/");

        var directory = Path.GetDirectoryName(loading.Peek())!;
        var candidate = Path.GetFullPath(Path.Combine(directory, path));
        var file = candidate.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? candidate
            : File.Exists(candidate + Extension) ? candidate + Extension
            : Directory.Exists(candidate) ? Path.Combine(candidate, "init" + Extension)
            : candidate + Extension;

        var stubs = Path.Combine(context.Workspace.Folder, InfraWorkspace.StubsFolderName);
        if (!InfraWorkspace.IsInside(context.Workspace.Folder, file) || InfraWorkspace.IsInside(stubs, file))
            throw context.Fail(DiagnosticCodes.RequireDenied,
                $"require(\"{path}\") resolves to {context.Workspace.RelativePath(file)}, outside {RepoLocator.InfraFolderName}/; only files inside it can be required");
        return file;
    }
}
