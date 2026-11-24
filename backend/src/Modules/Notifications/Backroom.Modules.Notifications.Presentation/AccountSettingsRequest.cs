namespace Backroom.Modules.Notifications.Presentation;

/// <summary>Body of <c>PUT /notification-settings</c>.</summary>
public sealed record AccountSettingsRequest(string? DefaultLevel, DateTimeOffset? OrgMutedUntil, bool Sound = true, bool HidePreviews = false);
