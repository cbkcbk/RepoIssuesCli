namespace RepoDocsCli.Features.Docs;

public static class DocsTime
{
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
    public const string DateFormat = "yyyy-MM-dd";

    public static DateTimeOffset Now(TimeProvider clock) => Truncate(clock.GetUtcNow());

    public static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
    }
}
