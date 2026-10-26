namespace RepoCli.Core.Features.Anchors;

enum BodyKind
{
    None,
    Block,
    Arrow,
    Semicolon
}

/// <remarks>Line numbers here are 0-based indexes into the masked lines.</remarks>
sealed class DeclarationExtent(MaskedLine[] lines)
{
    const int MaxHeaderLines = 40;

    static readonly string[] HeaderContinuations = ["{", ":", "=>", "->", ",", ".", "where", "throws", "extends", "implements"];

    static readonly string[] ExpressionContinuations = [".", "?", ":", "|", "&", "+", "-", "*", "/", "=>"];

    public (BodyKind Body, int End) Header(int start)
    {
        var depth = 0;
        var limit = Math.Min(lines.Length, start + MaxHeaderLines);
        for (var line = start; line < limit; line++)
        {
            var code = lines[line].Code;
            for (var column = 0; column < code.Length; column++)
            {
                switch (code[column])
                {
                    case '(' or '[':
                        depth++;
                        break;
                    case ')' or ']':
                        depth = Math.Max(0, depth - 1);
                        break;
                    case '{' when depth == 0:
                        return (BodyKind.Block, BlockEnd(line, column));
                    case ';' when depth == 0:
                        return (BodyKind.Semicolon, line);
                    case '=' when depth == 0 && At(code, column + 1) == '>':
                        return (BodyKind.Arrow, ArrowEnd(line, column + 2));
                }
            }

            if (depth == 0 && !NextLineStartsWithAny(line, HeaderContinuations))
                return (BodyKind.None, line);
        }

        return (BodyKind.None, start);
    }

    public int IndentedEnd(int start)
    {
        var headerEnd = start;
        var depth = 0;
        for (var line = start; line < lines.Length && line < start + MaxHeaderLines; line++)
        {
            depth += BracketBalance(lines[line].Code);
            headerEnd = line;
            if (depth <= 0)
                break;
        }

        var indent = lines[start].Indent;
        var end = headerEnd;
        depth = 0;
        for (var line = headerEnd + 1; line < lines.Length; line++)
        {
            var current = lines[line];
            if (current.IsBlank)
                continue;
            if (depth <= 0 && !current.Continued && current.Indent <= indent)
                break;
            end = line;
            depth += BracketBalance(current.Code);
        }

        return end;
    }

    int BlockEnd(int line, int column)
    {
        var depth = 0;
        for (; line < lines.Length; line++, column = 0)
        {
            var code = lines[line].Code;
            for (; column < code.Length; column++)
            {
                if (code[column] == '{')
                    depth++;
                else if (code[column] == '}' && --depth == 0)
                    return line;
            }
        }

        return lines.Length - 1;
    }

    int ArrowEnd(int line, int column)
    {
        var depth = 0;
        var seenCode = false;
        for (; line < lines.Length; line++, column = 0)
        {
            var code = lines[line].Code;
            for (; column < code.Length; column++)
            {
                var current = code[column];
                if (char.IsWhiteSpace(current))
                    continue;
                if (!seenCode && current == '{')
                    return BlockEnd(line, column);
                seenCode = true;
                switch (current)
                {
                    case '(' or '[' or '{':
                        depth++;
                        break;
                    case ')' or ']' or '}' when depth == 0:
                    case ';' when depth == 0:
                        return line;
                    case ')' or ']' or '}':
                        depth--;
                        break;
                }
            }

            if (seenCode && depth == 0 && !EndsWithOperator(code) && !NextLineStartsWithAny(line, ExpressionContinuations))
                return line;
        }

        return lines.Length - 1;
    }

    static bool EndsWithOperator(string code) =>
        code.TrimEnd() is [.., var last] && "=,(+-*/&|?:.<>%!".Contains(last);

    bool NextLineStartsWithAny(int line, string[] prefixes)
    {
        for (var next = line + 1; next < lines.Length && next <= line + MaxHeaderLines; next++)
        {
            if (lines[next].IsBlank)
                continue;
            var code = lines[next].Code.TrimStart();
            return prefixes.Any(prefix => code.StartsWith(prefix, StringComparison.Ordinal)
                                          && (!char.IsLetter(prefix[0]) || code.Length == prefix.Length || !char.IsLetterOrDigit(code[prefix.Length])));
        }

        return false;
    }

    static int BracketBalance(string code) =>
        code.Sum(current => current switch
        {
            '(' or '[' or '{' => 1,
            ')' or ']' or '}' => -1,
            _ => 0
        });

    static char At(string code, int index) => index < code.Length ? code[index] : '\0';
}
