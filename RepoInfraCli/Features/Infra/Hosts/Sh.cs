namespace RepoInfraCli.Features.Infra.Hosts;

public static class Sh
{
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static string Join(IEnumerable<string> values) => string.Join(' ', values.Select(Quote));

    public static IEnumerable<(string Key, string Value)> Lines(string stdout) =>
        stdout.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => line.IndexOf(' ') is var space and > 0 ? (line[..space], line[(space + 1)..]) : (line, ""));
}
