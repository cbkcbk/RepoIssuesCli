namespace RepoCli.Core.Features.Text;

public static class TokenEstimate
{
    public const string Method = "max(ceil(chars / 4), ceil(words * 4 / 3)); a line ending counts as one char, words are runs of non-whitespace";

    public static int Of(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var normalized = text.TrimStart('﻿').ReplaceLineEndings("\n");
        var byChars = (normalized.Length + 3) / 4;
        var byWords = (Words(normalized) * 4 + 2) / 3;
        return Math.Max(byChars, byWords);
    }

    static int Words(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var character in text)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (!isSpace && !inWord)
                count++;
            inWord = !isSpace;
        }

        return count;
    }
}
