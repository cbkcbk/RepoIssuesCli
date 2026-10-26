using System.Globalization;
using System.Text;
using RepoCli.Core.Features.Repo;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace RepoCli.Core.Features.IssueLinks;

/// <summary>Reads and rewrites the issue numbers in <c>.repo-docs</c> plan steps without re-serializing the files.</summary>
public sealed class DocsSteps(string root)
{
    const string Extension = ".yaml";
    const string ConfigFileName = "config" + Extension;
    static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string Folder => Path.Combine(root, RepoLocator.DocsFolderName);

    public bool Exists => Directory.Exists(Folder);

    public IDisposable Lock() => Exists ? FolderLock.Acquire(Folder, LockTimeout) : new NoLease();

    public DocsReferenceScan Find()
    {
        if (!Exists)
            return DocsReferenceScan.Empty;

        var references = new List<DocsReference>();
        var errors = new List<DocsReferenceError>();
        foreach (var path in Files())
        {
            if (TryParse(path, errors) is { } file)
                references.AddRange(file.Numbers.Select(number => number.Reference));
        }

        return new DocsReferenceScan(references, errors);
    }

    public DocsReferenceScan Find(int number) => Find().To(number);

    /// <remarks>The caller holds <see cref="Lock"/>, taken after the issues lock.</remarks>
    /// <returns>The rewritten references, now pointing at <paramref name="to"/>.</returns>
    public DocsReferenceScan Renumber(int from, int to)
    {
        if (!Exists || from == to)
            return DocsReferenceScan.Empty;

        var rewritten = new List<DocsReference>();
        var errors = new List<DocsReferenceError>();
        foreach (var path in Files())
        {
            if (TryParse(path, errors) is not { } file)
                continue;

            var matches = file.Numbers.Where(number => number.Reference.IssueNumber == from).ToList();
            if (matches.Count == 0)
                continue;

            try
            {
                Write(path, file.Bom, Replace(file.Text, matches, to));
                rewritten.AddRange(matches.Select(match => match.Reference with { IssueNumber = to }));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add(new DocsReferenceError(file.RepoPath, exception.Message));
            }
        }

        return new DocsReferenceScan(rewritten, errors);
    }

    IEnumerable<string> Files() =>
        Directory.EnumerateFiles(Folder, "*" + Extension)
            .Where(path => Path.GetFileName(path) != ConfigFileName)
            .Order(StringComparer.Ordinal);

    DocsFile? TryParse(string path, List<DocsReferenceError> errors)
    {
        var repoPath = Path.GetRelativePath(root, path).Replace('\\', '/');
        try
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.AsSpan().StartsWith(Utf8Bom);
            var text = StrictUtf8.GetString(bytes, bom ? Utf8Bom.Length : 0, bytes.Length - (bom ? Utf8Bom.Length : 0));
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            return new DocsFile(repoPath, bom, text, Numbers(stream, Path.GetFileNameWithoutExtension(path), repoPath).ToList());
        }
        catch (Exception exception) when (exception is YamlException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new DocsReferenceError(repoPath, exception.Message));
            return null;
        }
    }

    static IEnumerable<NumberScalar> Numbers(YamlStream stream, string stem, string repoPath)
    {
        if (stream.Documents is not [{ RootNode: YamlMappingNode document }, ..] || Child(document, "entries") is not YamlSequenceNode entries)
            yield break;

        foreach (var (index, node) in entries.Children.Index())
        {
            if (node is not YamlMappingNode entry || Child(entry, "steps") is not YamlSequenceNode steps)
                continue;

            var address = $"{stem}/{Scalar(entry, "id") ?? $"#{index + 1}"}";
            foreach (var step in steps.Children.OfType<YamlMappingNode>())
            {
                if (Child(step, "issues") is not YamlSequenceNode issues)
                    continue;

                var title = Scalar(step, "title") ?? "";
                foreach (var scalar in issues.Children.OfType<YamlScalarNode>())
                {
                    if (int.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                        yield return new NumberScalar(new DocsReference(address, title, number, repoPath), scalar);
                }
            }
        }
    }

    static YamlNode? Child(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    static string? Scalar(YamlMappingNode mapping, string key) => Child(mapping, key) is YamlScalarNode { Value: { } value } ? value.Trim() : null;

    static string Replace(string text, IReadOnlyList<NumberScalar> matches, int to)
    {
        var builder = new StringBuilder(text);
        foreach (var scalar in matches.Select(match => match.Node).DistinctBy(node => node.Start.Index).OrderByDescending(node => node.Start.Index))
        {
            var start = (int)scalar.Start.Index;
            var length = (int)scalar.End.Index - start;
            if (Spell(scalar.Style, scalar.Value!) is not { } original || start + length > text.Length || text.Substring(start, length) != original)
                throw new InvalidDataException($"Can't rewrite issue {scalar.Value} at line {scalar.Start.Line} in place; edit it by hand.");

            builder.Remove(start, length).Insert(start, Spell(scalar.Style, to.ToString(CultureInfo.InvariantCulture)));
        }

        return builder.ToString();
    }

    static string? Spell(ScalarStyle style, string value) => style switch
    {
        ScalarStyle.Plain => value,
        ScalarStyle.SingleQuoted => $"'{value}'",
        ScalarStyle.DoubleQuoted => $"\"{value}\"",
        _ => null,
    };

    static void Write(string path, bool bom, string text)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            if (bom)
                stream.Write(Utf8Bom);
            stream.Write(StrictUtf8.GetBytes(text));
        }

        File.Move(temporary, path, overwrite: true);
    }

    sealed record NumberScalar(DocsReference Reference, YamlScalarNode Node);

    sealed record DocsFile(string RepoPath, bool Bom, string Text, IReadOnlyList<NumberScalar> Numbers);

    sealed class NoLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
