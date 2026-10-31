namespace RepoInfraCli.Features.Infra.Hosts;

public sealed record HostFacts(string Id, string VersionId, string Codename)
{
    public const string ProbeCommand = "cat /etc/os-release; echo \"INFRA_SSH_PORT=${SSH_CONNECTION##* }\"";

    public static readonly IReadOnlyList<string> SupportedVersions = ["22.04", "24.04"];

    public int? SshPort { get; init; }

    public bool IsSupported => Id == "ubuntu" && SupportedVersions.Contains(VersionId);

    public string Display => $"{Id} {VersionId}".Trim();

    public static HostFacts Parse(string osRelease)
    {
        var values = osRelease.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => (Key: line[..line.IndexOf('=')], Value: line[(line.IndexOf('=') + 1)..].Trim('"', '\'')))
            .GroupBy(pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.Last().Value);
        return new HostFacts(values.GetValueOrDefault("ID", ""), values.GetValueOrDefault("VERSION_ID", ""), values.GetValueOrDefault("VERSION_CODENAME", ""))
        {
            SshPort = int.TryParse(values.GetValueOrDefault("INFRA_SSH_PORT"), System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : null,
        };
    }

    public void EnsureSupported(string host)
    {
        if (!IsSupported)
            throw new HostException(host, $"host '{host}' runs {(Display.Length == 0 ? "an unknown OS" : Display)}; only Ubuntu {string.Join(" and ", SupportedVersions)} are supported");
    }
}
