namespace Backroom.Modules.Messaging.Presentation;

public sealed record MessageResponse(Guid Id, long Seq, Guid ClientMessageId, Guid AuthorAccountId, string Text, DateTimeOffset CreatedAt);
