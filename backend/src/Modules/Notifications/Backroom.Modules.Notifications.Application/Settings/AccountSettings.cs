namespace Backroom.Modules.Notifications.Application.Settings;

/// <summary>A person's settings for the whole organization; a person without a row has <see cref="Default"/>.</summary>
/// <param name="DefaultLevel">What applies where a conversation has no setting of its own.</param>
/// <param name="OrgMutedUntil">Notifications of the organization are silent until then (they still reach the bell).</param>
/// <param name="Sound">Whether the client plays a sound.</param>
/// <param name="HidePreviews">The live notification carries no text.</param>
internal sealed record AccountSettings(NotificationLevel DefaultLevel, DateTimeOffset? OrgMutedUntil, bool Sound, bool HidePreviews)
{
    public static AccountSettings Default { get; } = new(NotificationLevel.All, null, true, false);
}
