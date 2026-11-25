namespace Backroom.Modules.Messaging.Presentation;

public sealed record ConversationMembersResponse(IReadOnlyList<ConversationMemberResponse> Items, string? Next);
