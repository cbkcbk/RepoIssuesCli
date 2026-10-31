using System.Globalization;
using System.Text.RegularExpressions;

namespace RepoInfraCli.Features.Infra.Postgres;

public sealed record BackupDump(string Key, long Size, DateTimeOffset Time)
{
    public string TimeText => Time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => Scaled(1024.0, "KB"),
        < 1024L * 1024 * 1024 => Scaled(1024.0 * 1024, "MB"),
        _ => Scaled(1024.0 * 1024 * 1024, "GB"),
    };

    string Scaled(double unit, string name) => string.Create(CultureInfo.InvariantCulture, $"{Size / unit:0.0} {name}");
}

public sealed class BackupNotFoundException(string message) : NotFoundException(message);

public static partial class BackupDumps
{
    const string KeyTimeFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static IReadOnlyList<BackupDump> Parse(string listing) =>
    [
        .. listing.Split('\n')
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(fields => fields is [{ Length: > 0 }, _, _])
            .Select(fields => new BackupDump(
                fields[0],
                long.TryParse(fields[1], CultureInfo.InvariantCulture, out var size) ? size : 0,
                KeyTime(fields[0]) ?? ParseTime(fields[2]) ?? DateTimeOffset.MinValue))
            .OrderBy(dump => dump.Time)
            .ThenBy(dump => dump.Key, StringComparer.Ordinal),
    ];

    public static DateTimeOffset? ParseTime(string text)
    {
        var trimmed = text.Trim();
        if (DateTimeOffset.TryParseExact(trimmed, KeyTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var compact))
            return compact;
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    public static BackupDump Select(IReadOnlyList<BackupDump> dumps, string database, string location, string? key, DateTimeOffset? at)
    {
        if (key is not null)
            return dumps.FirstOrDefault(dump => dump.Key == key)
                   ?? throw new BackupNotFoundException($"No dump '{key}' of {database} in {location}; run 'infra restore {database} --list' to see the dumps.");

        if (dumps.Count == 0)
            throw new BackupNotFoundException($"No dumps of {database} in {location} yet; the backup sidecar writes them on its schedule.");

        if (at is not { } time)
            return dumps[^1];

        return dumps.LastOrDefault(dump => dump.Time <= time)
               ?? throw new BackupNotFoundException($"No dump of {database} at or before {time.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC; the oldest in {location} is {dumps[0].Key} ({dumps[0].TimeText}).");
    }

    static DateTimeOffset? KeyTime(string key) => KeyTimestamp().Match(key) is { Success: true } match ? ParseTime(match.Groups[1].Value) : null;

    [GeneratedRegex(@"-(\d{8}T\d{6}Z)\.dump$")]
    private static partial Regex KeyTimestamp();
}
