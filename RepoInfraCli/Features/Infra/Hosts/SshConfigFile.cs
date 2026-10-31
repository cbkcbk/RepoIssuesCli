using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra.Hosts;

public static class SshConfigFile
{
    public static string? ProxyCommand(IEnumerable<string> configFiles, string host)
    {
        foreach (var file in configFiles)
        {
            if (Find(file, host, "proxycommand", depth: 0) is { } value)
                return value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value;
        }
        return null;
    }

    static string? Find(string file, string host, string keyword, int depth)
    {
        if (depth > 8 || !File.Exists(file))
            return null;

        var matches = true;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var (key, value) = Split(line);
            switch (key)
            {
                case "host":
                    matches = HostMatches(value, host);
                    break;
                case "match":
                    matches = value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
                    break;
                case "include" when matches:
                    foreach (var included in Includes(file, value))
                    {
                        if (Find(included, host, keyword, depth + 1) is { } found)
                            return found;
                    }
                    break;
                default:
                    if (matches && key == keyword)
                        return value.Trim();
                    break;
            }
        }
        return null;
    }

    static (string Key, string Value) Split(string line)
    {
        var index = line.IndexOfAny([' ', '\t', '=']);
        if (index < 0)
            return (line.ToLowerInvariant(), "");
        var value = line[index..].TrimStart(' ', '\t', '=').TrimStart();
        return (line[..index].ToLowerInvariant(), value);
    }

    public static bool HostMatches(string patterns, string host)
    {
        var matched = false;
        foreach (var pattern in patterns.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var negated = pattern.StartsWith('!');
            if (!Glob(negated ? pattern[1..] : pattern).IsMatch(host))
                continue;
            if (negated)
                return false;
            matched = true;
        }
        return matched;
    }

    static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static IEnumerable<string> Includes(string file, string value)
    {
        var sshFolder = Path.GetDirectoryName(file) ?? ".";
        foreach (var entry in value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var expanded = entry.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), entry[2..])
                : Path.IsPathRooted(entry) ? entry : Path.Combine(sshFolder, entry);
            var folder = Path.GetDirectoryName(expanded) ?? ".";
            var name = Path.GetFileName(expanded);
            if (!name.Contains('*') && !name.Contains('?'))
            {
                yield return expanded;
                continue;
            }
            if (!Directory.Exists(folder))
                continue;
            foreach (var match in Directory.EnumerateFiles(folder, name).Order(StringComparer.Ordinal))
                yield return match;
        }
    }
}
