namespace Backroom.Modules.Messaging.Application;

internal static class StoredTime
{
    /// <summary>
    /// The time as the database keeps it: PostgreSQL stores microseconds, .NET ticks are 100 ns, so an answer built from
    /// the unrounded time would differ from the stored row (and from a repeat that reads it back).
    /// </summary>
    public static DateTimeOffset Now(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);
    }
}
