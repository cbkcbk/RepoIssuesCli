using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoCli.Core.Features.Anchors;

public static partial class Fingerprint
{
    public const int HashLength = 12;

    public static string Normalize(string line) => Whitespace.Replace(line.Trim(), " ");

    public static string Of(IEnumerable<string> normalizedLines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalizedLines))))[..HashLength];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }
}
