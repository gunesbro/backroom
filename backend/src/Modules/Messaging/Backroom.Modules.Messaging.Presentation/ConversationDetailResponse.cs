namespace Backroom.Modules.Messaging.Presentation;

public sealed record ConversationDetailResponse(
    Guid Id, string Kind, string? Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset? ArchivedAt, int MemberCount, Guid? OwnerAccountId, string? Role);
