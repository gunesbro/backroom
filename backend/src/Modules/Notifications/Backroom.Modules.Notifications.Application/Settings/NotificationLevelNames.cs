namespace Backroom.Modules.Notifications.Application.Settings;

/// <summary>The names the database and the API use for <see cref="NotificationLevel"/>.</summary>
internal static class NotificationLevelNames
{
    public static string Of(NotificationLevel level) => level switch
    {
        NotificationLevel.All => "all",
        NotificationLevel.Mentions => "mentions",
        NotificationLevel.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static bool TryParse(string? name, out NotificationLevel level)
    {
        switch (name)
        {
            case "all":
                level = NotificationLevel.All;
                return true;
            case "mentions":
                level = NotificationLevel.Mentions;
                return true;
            case "none":
                level = NotificationLevel.None;
                return true;
            default:
                level = default;
                return false;
        }
    }
}
