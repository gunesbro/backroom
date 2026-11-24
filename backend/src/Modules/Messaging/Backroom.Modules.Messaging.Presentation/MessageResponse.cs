namespace Backroom.Modules.Messaging.Presentation;

public sealed record MessageResponse(Guid Id, Guid ClientMessageId, Guid AuthorAccountId, string Text, DateTimeOffset CreatedAt);
