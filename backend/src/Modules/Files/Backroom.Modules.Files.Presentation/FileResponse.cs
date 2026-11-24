namespace Backroom.Modules.Files.Presentation;

public sealed record FileResponse(Guid Id, Guid ConversationId, string Name, long Size, string ContentType, string Kind, string Status, DateTimeOffset CreatedAt);
