namespace Backroom.Modules.Messaging.Presentation;

public sealed record ConversationsResponse(IReadOnlyList<ConversationResponse> Items, Guid? Next);
