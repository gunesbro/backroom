namespace Backroom.Modules.Notifications.Application;

internal static class StoredTime
{
    /// <summary>The time as the database keeps it (microseconds), so what is answered equals what is stored.</summary>
    public static DateTimeOffset Now(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % (TimeSpan.TicksPerMillisecond / 1000)), TimeSpan.Zero);
    }
}
