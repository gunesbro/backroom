namespace Backroom.Modules.Notifications.Application.Settings;

/// <summary>A person's settings for one conversation; without a row the account settings apply.</summary>
internal sealed record ConversationSettings(NotificationLevel Level, bool FollowedThreadReplies, bool IgnoreGroupMentions, bool Sound);
