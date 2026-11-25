namespace Backroom.Modules.Notifications.Presentation;

/// <summary>The conversation's own settings; <c>level</c> is null while it has none and the account settings apply.</summary>
public sealed record ConversationSettingsResponse(string? Level, bool FollowedThreadReplies, bool IgnoreGroupMentions, bool Sound);
