using System.Text.RegularExpressions;

namespace RepoCli.Core.Features.Anchors;

static partial class SymbolScanner
{
    enum Origin
    {
        Type,
        Indented,
        Keyword,
        Assigned,
        Signature
    }

    sealed record Candidate(string Name, SymbolKind Kind, Origin Origin, string? Receiver = null, bool Qualified = false);

    const string Attribute = @"(?:@[\w.]+(?:\([^)]*\))?|#?\[[^\]]*\])";

    const string Modifier =
        @"(?:public|private|protected|internal|static|abstract|sealed|partial|readonly|unsafe|file|new|export|default|declare|data|open|final|inner|value|inline|annotation|companion|pub(?:\([^)]*\))?|async|extern|ref|const|virtual|override|required|strictfp|synchronized|native|transient|volatile|suspend|operator|infix|tailrec|external|get|set|implicit|explicit|constexpr|actual|expect)\b";

    const string Prefix = "(?:(?:" + Attribute + "|" + Modifier + "|\"\\s*\")\\s*)*?";

    const string Name = @"(?<name>[A-Za-z_$][\w$]*)";

    static readonly HashSet<string> NotNames =
    [
        "if", "else", "elif", "elsif", "for", "foreach", "while", "do", "switch", "case", "when", "match", "catch", "try", "finally",
        "using", "return", "lock", "fixed", "with", "sizeof", "typeof", "nameof", "checked", "unchecked", "synchronized", "throw",
        "await", "yield", "select", "loop", "assert", "guard", "defer", "go", "unless", "until", "except", "new", "super", "this",
        "base", "function", "func", "fn", "def", "fun", "lambda", "in", "is", "as", "not", "and", "or", "instanceof", "delete",
        "default", "goto", "print", "echo", "require", "import"
    ];

    public static IReadOnlyList<Declaration> Scan(SourceText text)
    {
        var lines = CodeMask.Apply(text.Lines);
        var extent = new DeclarationExtent(lines);
        var declarations = new List<Declaration>();
        var open = new Stack<Declaration>();
        for (var index = 0; index < lines.Length; index++)
        {
            if (Detect(lines[index].Code.Trim()) is not { } candidate || EndOf(candidate, index, extent) is not { } end)
                continue;

            var line = index + 1;
            while (open.TryPeek(out var top) && top.EndLine < line)
                open.Pop();
            var parent = open.TryPeek(out var enclosing) ? enclosing : null;

            var kind = candidate.Kind == SymbolKind.Function && (candidate.Receiver is not null || parent?.IsType == true)
                ? SymbolKind.Method
                : candidate.Kind;
            var container = candidate.Receiver ?? parent?.Symbol;
            var declaration = new Declaration(
                candidate.Name,
                kind,
                line,
                Math.Min(end + 1, parent?.EndLine ?? int.MaxValue),
                container is null ? candidate.Name : $"{container}.{candidate.Name}");
            declarations.Add(declaration);
            open.Push(declaration);
        }

        return declarations;
    }

    static int? EndOf(Candidate candidate, int line, DeclarationExtent extent)
    {
        if (candidate.Origin == Origin.Indented)
            return extent.IndentedEnd(line);

        var (body, end) = extent.Header(line);
        var accepted = candidate.Origin switch
        {
            Origin.Type or Origin.Keyword => true,
            Origin.Assigned => body is BodyKind.Block or BodyKind.Arrow,
            _ => body == BodyKind.Block || (body == BodyKind.Arrow && candidate.Qualified)
        };
        return accepted ? end : null;
    }

    static Candidate? Detect(string code)
    {
        if (code.Length == 0)
            return null;

        if (ImplHead.Match(code) is { Success: true } impl)
            return ImplCandidate(code[impl.Length..]);

        if (GoType.Match(code) is { Success: true } goType)
            return new Candidate(goType.Groups["name"].Value, KindOf(goType.Groups["kind"].Value), Origin.Type);

        if (TypeDeclaration.Match(code) is { Success: true } type)
        {
            var indented = code.EndsWith(':') && !code.Contains('{');
            return new Candidate(type.Groups["name"].Value, KindOf(type.Groups["kind"].Value), indented ? Origin.Indented : Origin.Type);
        }

        if (KeywordFunction.Match(code) is { Success: true } function)
        {
            var keyword = function.Groups["keyword"].Value;
            var receiver = function.Groups["receiver"];
            if ((receiver.Success && keyword != "func") || NotNames.Contains(function.Groups["name"].Value))
                return null;
            return new Candidate(
                function.Groups["name"].Value,
                SymbolKind.Function,
                keyword == "def" ? Origin.Indented : Origin.Keyword,
                receiver.Success ? ReceiverType.Match(receiver.Value).Groups["name"].Value : null);
        }

        if (AssignedFunction.Match(code) is { Success: true } assigned)
            return NotNames.Contains(assigned.Groups["name"].Value)
                ? null
                : new Candidate(assigned.Groups["name"].Value, SymbolKind.Function, Origin.Assigned);

        if (Signature.Match(code) is { Success: true } signature)
        {
            var name = signature.Groups["name"].Value;
            var returnType = signature.Groups["type"].Value;
            if (NotNames.Contains(name) || WordSplit.Split(returnType).Any(NotNames.Contains))
                return null;
            var qualified = returnType.Length > 0 || signature.Groups["mods"].Length > 0;
            return new Candidate(name, SymbolKind.Function, Origin.Signature, Qualified: qualified);
        }

        return null;
    }

    static Candidate? ImplCandidate(string rest)
    {
        rest = SkipGenericArguments(rest.TrimStart());
        if (ImplFor.Match(rest) is { Success: true } forClause)
            rest = rest[(forClause.Index + forClause.Length)..];
        var path = ImplTarget.Match(rest);
        if (!path.Success)
            return null;
        var name = path.Groups["path"].Value.Split("::")[^1];
        return new Candidate(name, SymbolKind.Impl, Origin.Type);
    }

    static string SkipGenericArguments(string text)
    {
        if (!text.StartsWith('<'))
            return text;
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '-' && index + 1 < text.Length && text[index + 1] == '>')
                index++;
            else if (text[index] == '<')
                depth++;
            else if (text[index] == '>' && --depth == 0)
                return text[(index + 1)..].TrimStart();
        }

        return text;
    }

    static SymbolKind KindOf(string keyword) => keyword switch
    {
        "class" => SymbolKind.Class,
        "struct" or "union" => SymbolKind.Struct,
        "record" => SymbolKind.Record,
        "interface" => SymbolKind.Interface,
        "enum" => SymbolKind.Enum,
        "trait" => SymbolKind.Trait,
        _ => throw new ArgumentOutOfRangeException(nameof(keyword), keyword, null)
    };

    [GeneratedRegex(@"^(?:unsafe\s+)?impl\b")]
    private static partial Regex ImplHead { get; }

    [GeneratedRegex(@"\bfor\s+")]
    private static partial Regex ImplFor { get; }

    [GeneratedRegex(@"^&?(?:dyn\s+)?(?<path>[A-Za-z_][\w:]*)")]
    private static partial Regex ImplTarget { get; }

    [GeneratedRegex(@"^type\s+" + Name + @"(?:\[[^\]]*\])?\s+(?<kind>struct|interface)\b")]
    private static partial Regex GoType { get; }

    [GeneratedRegex("^" + Prefix + @"(?<kind>class|struct|record|interface|enum|trait|union)\s+(?:(?:class|struct)\s+)?" + Name)]
    private static partial Regex TypeDeclaration { get; }

    [GeneratedRegex("^" + Prefix + @"(?<keyword>function|def|fn|func|fun)\b\s*\*?\s*(?:\((?<receiver>[^)]*)\)\s*)?(?:<[^>]*>\s*)?" + Name + @"\s*[<(\[]")]
    private static partial Regex KeywordFunction { get; }

    [GeneratedRegex(@"(?<name>[A-Za-z_]\w*)(?:\[[^\]]*\])?\s*$")]
    private static partial Regex ReceiverType { get; }

    [GeneratedRegex(@"^(?:(?:export|const|let|var|static|readonly|public|private|protected|declare)\s+)*" + Name +
                    @"\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^()]*\)\s*(?::[^=]+)?=>|[A-Za-z_$][\w$]*\s*=>)")]
    private static partial Regex AssignedFunction { get; }

    [GeneratedRegex(@"^(?<mods>(?:(?:" + Attribute + "|" + Modifier + @")\s*)*)(?:(?<type>(?:\([^()]*\)\??|[\w.<>\[\],?*&:])[\w.<>\[\],?*&:\s]*?)\s+)?" + Name +
                    @"\s*(?:<[^()]*>\s*)?\(")]
    private static partial Regex Signature { get; }

    [GeneratedRegex(@"\W+")]
    private static partial Regex WordSplit { get; }
}
