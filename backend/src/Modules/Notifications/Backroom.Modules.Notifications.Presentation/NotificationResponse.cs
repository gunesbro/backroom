namespace Backroom.Modules.Notifications.Presentation;

/// <summary>A bell entry; <c>preview</c> is null when there is none or the person can no longer read the message.</summary>
public sealed record NotificationResponse(
    Guid Id, string Kind, Guid? ConversationId, Guid? MessageId, Guid ActorAccountId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, string? Preview);
