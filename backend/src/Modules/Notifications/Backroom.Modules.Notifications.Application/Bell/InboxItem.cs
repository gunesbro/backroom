namespace Backroom.Modules.Notifications.Application.Bell;

/// <summary>A stored bell entry.</summary>
internal sealed record InboxItem(
    Guid Id, string Kind, Guid? ConversationId, Guid? MessageId, Guid ActorAccountId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
