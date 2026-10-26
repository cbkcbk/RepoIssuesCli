namespace RepoCli.Core.Features.Anchors;

static class CodeMask
{
    sealed record Literal(string Closer, bool Escapes = false, bool DoubledQuotes = false, bool Multiline = false, bool Comment = false);

    static readonly Literal BlockComment = new("*/", Multiline: true, Comment: true);

    public static MaskedLine[] Apply(IReadOnlyList<string> lines)
    {
        var masked = new MaskedLine[lines.Count];
        Literal? open = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var chars = lines[index].ToCharArray();
            var continued = open is not null;
            open = MaskLine(chars, open);
            masked[index] = new MaskedLine(new string(chars), continued);
        }

        return masked;
    }

    static Literal? MaskLine(char[] chars, Literal? open)
    {
        var index = 0;
        if (open is not null)
        {
            index = Close(chars, 0, open);
            if (index < 0)
                return open;
        }

        while (index < chars.Length)
        {
            var next = At(chars, index + 1);
            switch (chars[index])
            {
                case '/' when next == '/':
                case '#' when IsHashComment(chars, index):
                    Blank(chars, index, chars.Length);
                    return null;
                case '/' when next == '*':
                    Blank(chars, index, index + 2);
                    index = Close(chars, index + 2, BlockComment);
                    if (index < 0)
                        return BlockComment;
                    break;
                case '"' or '\'' or '`':
                    var (literal, start) = Opening(chars, index);
                    if (literal is null)
                    {
                        index = start;
                        break;
                    }

                    index = Close(chars, start, literal);
                    if (index < 0)
                        return literal.Multiline ? literal : null;
                    break;
                default:
                    index++;
                    break;
            }
        }

        return null;
    }

    static (Literal? Literal, int Start) Opening(char[] chars, int index)
    {
        var quote = chars[index];
        if (quote == '`')
            return (new Literal("`", Escapes: true, Multiline: true), index + 1);

        var run = 0;
        while (At(chars, index + run) == quote)
            run++;

        if (run >= 3)
            return (new Literal(new string(quote, run), Multiline: true), index + run);

        if (quote == '\'')
            return IsCharacterOrString(chars, index) ? (new Literal("'", Escapes: true), index + 1) : (null, index + 1);

        if (run == 2)
            return (null, index + 2);

        var verbatim = At(chars, index - 1) == '@' || (At(chars, index - 1) == '$' && At(chars, index - 2) == '@');
        return verbatim
            ? (new Literal("\"", DoubledQuotes: true, Multiline: true), index + 1)
            : (new Literal("\"", Escapes: true), index + 1);
    }

    static bool IsCharacterOrString(char[] chars, int index)
    {
        if (At(chars, index + 1) == '\\' || At(chars, index + 2) == '\'')
            return true;
        if (Array.IndexOf(chars, '\'', index + 1) < 0)
            return false;
        return !IsLifetime(chars, index);
    }

    static bool IsLifetime(char[] chars, int index)
    {
        var end = index + 1;
        while (end < chars.Length && (char.IsLetterOrDigit(chars[end]) || chars[end] == '_'))
            end++;
        if (end == index + 1 || At(chars, end) == '\'')
            return false;

        var before = index - 1;
        while (before >= 0 && chars[before] == ' ')
            before--;
        var previous = At(chars, before);
        var following = At(chars, end);
        return previous is '&' or '<'
            || (previous is ',' or '+' or ':' or '(' && following is '>' or ',' or '+' or ')' or ';' or '\0')
            || (previous == ':' && following == ' ');
    }

    static bool IsHashComment(char[] chars, int index)
    {
        var next = At(chars, index + 1);
        return next is '\0' or '#' || char.IsWhiteSpace(next) || (index == 0 && next == '!' && At(chars, 2) != '[');
    }

    static int Close(char[] chars, int start, Literal literal)
    {
        for (var index = start; index < chars.Length; index++)
        {
            if (literal.Escapes && chars[index] == '\\')
            {
                Blank(chars, index, index + 2);
                index++;
                continue;
            }

            if (Matches(chars, index, literal.Closer))
            {
                if (literal.DoubledQuotes && At(chars, index + 1) == '"')
                {
                    Blank(chars, index, index + 2);
                    index++;
                    continue;
                }

                var end = index + literal.Closer.Length;
                if (literal.Comment)
                    Blank(chars, index, end);
                return end;
            }

            chars[index] = ' ';
        }

        return -1;
    }

    static bool Matches(char[] chars, int index, string closer) =>
        index + closer.Length <= chars.Length && chars.AsSpan(index, closer.Length).SequenceEqual(closer);

    static void Blank(char[] chars, int start, int end) =>
        chars.AsSpan(start, Math.Min(end, chars.Length) - start).Fill(' ');

    static char At(char[] chars, int index) => index >= 0 && index < chars.Length ? chars[index] : '\0';
}
