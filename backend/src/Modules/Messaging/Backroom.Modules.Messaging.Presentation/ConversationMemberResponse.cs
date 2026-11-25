namespace Backroom.Modules.Messaging.Presentation;

public sealed record ConversationMemberResponse(Guid AccountId, string Role, DateTimeOffset JoinedAt);
