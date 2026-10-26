using RepoCli.Core.Features.Text;

namespace RepoCli.Core.Tests.Features.Text;

public class TokenEstimateTests
{
    [TestCase(null, 0)]
    [TestCase("", 0)]
    [TestCase("abcd", 2)]
    [TestCase("hello world", 3)]
    [TestCase("a b c d e f", 8)]
    [TestCase("   ", 1)]
    [TestCase("Use MidpointRounding.ToEven when rounding money.", 12)]
    public void EstimatesTheLargerOfCharsAndWords(string? text, int expected) =>
        Assert.That(TokenEstimate.Of(text), Is.EqualTo(expected));

    [Test]
    public void LongWordsAreCountedByChars() => Assert.That(TokenEstimate.Of(new string('x', 400)), Is.EqualTo(100));

    [Test]
    public void LineEndingsAndByteOrderMarkDoNotInflateTheEstimate()
    {
        var text = string.Join("\n", Enumerable.Repeat("abcdefghijklmnop", 10));

        Assert.That(TokenEstimate.Of(text.ReplaceLineEndings("\r\n")), Is.EqualTo(TokenEstimate.Of(text)));
        Assert.That(TokenEstimate.Of("﻿" + text), Is.EqualTo(TokenEstimate.Of(text)));
    }
}
