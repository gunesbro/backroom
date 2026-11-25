namespace Backroom.Modules.Notifications.Presentation;

/// <summary>Body of <c>PUT /conversations/{id}/notification-settings</c>.</summary>
public sealed record ConversationSettingsRequest(string? Level, bool FollowedThreadReplies = true, bool IgnoreGroupMentions = false, bool Sound = true);
