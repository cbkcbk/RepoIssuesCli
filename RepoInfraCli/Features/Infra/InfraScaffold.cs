using System.Text;
using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra;

public sealed record InfraInitResult(string Folder, bool Created, string Project, IReadOnlyList<string> Files, IReadOnlyList<string> Stubs);

public static partial class InfraScaffold
{
    const string StubResourcePrefix = "infra-stubs/";

    static readonly UTF8Encoding Utf8 = new(false);

    public static IReadOnlyList<string> StubNames { get; } =
        [.. typeof(InfraScaffold).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(StubResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[StubResourcePrefix.Length..])
            .Order(StringComparer.Ordinal)];

    public static string ReadStub(string name)
    {
        using var stream = typeof(InfraScaffold).Assembly.GetManifestResourceStream(StubResourcePrefix + name)
            ?? throw new InvalidOperationException($"Missing embedded stub {name}.");
        using var reader = new StreamReader(stream, Utf8);
        return reader.ReadToEnd();
    }

    public static InfraInitResult Initialize(InfraWorkspace workspace)
    {
        var created = !workspace.IsInitialized;
        var files = new List<string>();
        var stubs = new List<string>();
        var project = ProjectName(workspace.Root);

        Directory.CreateDirectory(workspace.Folder);
        if (created)
        {
            File.WriteAllText(workspace.EntryFile, StarterScript(project), Utf8);
            files.Add(workspace.RelativePath(workspace.EntryFile));
        }

        if (!File.Exists(workspace.LuauRcFile))
        {
            File.WriteAllText(workspace.LuauRcFile, LuauRc, Utf8);
            files.Add(workspace.RelativePath(workspace.LuauRcFile));
        }

        Directory.CreateDirectory(workspace.StubsFolder);
        foreach (var name in StubNames)
        {
            var target = Path.Combine(workspace.StubsFolder, name);
            var content = ReadStub(name);
            if (File.Exists(target) && File.ReadAllText(target, Utf8) == content)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, Utf8);
            stubs.Add(workspace.RelativePath(target));
        }

        return new InfraInitResult(workspace.Folder, created, project, files, stubs);
    }

    public static string ProjectName(string root)
    {
        var name = NonName().Replace(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).ToLowerInvariant(), "-").Trim('-');
        return name.Length == 0 ? "app" : name;
    }

    static string StarterScript(string project) => $$"""
        --!strict
        local infra = require("@infra")

        infra.project "{{project}}" {}

        local exports: infra.Exports = {}

        return exports

        """.ReplaceLineEndings("\n");

    static readonly string LuauRc = """
        {
            "languageMode": "strict",
            "aliases": {
                "infra": ".luau/@infra"
            }
        }

        """.ReplaceLineEndings("\n");

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NonName();
}
