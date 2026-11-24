namespace Backroom.Modules.Messaging.Presentation;

public sealed record MessagesResponse(IReadOnlyList<MessageResponse> Messages, long? Next);
