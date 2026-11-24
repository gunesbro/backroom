namespace Backroom.Modules.Files.Presentation;

public sealed record StartUploadRequest(Guid? ConversationId, string? Name, long? Size, string? ContentType);
