namespace Backroom.Modules.Notifications.Application.Bell;

/// <summary>A bell entry to store: ids only, never text.</summary>
internal sealed record NewInboxItem(
    Guid Id, Guid OrgId, Guid AccountId, string Kind, Guid? ConversationId, Guid? MessageId, Guid ActorAccountId, DateTimeOffset CreatedAt);
