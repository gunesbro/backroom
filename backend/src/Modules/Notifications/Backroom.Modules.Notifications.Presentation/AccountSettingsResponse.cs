namespace Backroom.Modules.Notifications.Presentation;

public sealed record AccountSettingsResponse(string DefaultLevel, DateTimeOffset? OrgMutedUntil, bool Sound, bool HidePreviews);
