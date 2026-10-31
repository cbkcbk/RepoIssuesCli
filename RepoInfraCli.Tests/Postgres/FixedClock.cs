namespace RepoInfraCli.Tests.Postgres;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public static FixedClock Default { get; } = new(new DateTimeOffset(2026, 10, 31, 1, 2, 3, TimeSpan.Zero));

    public override DateTimeOffset GetUtcNow() => now;
}
