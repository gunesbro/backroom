namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>An open membership in the member list.</summary>
internal sealed record ConversationMember(Guid AccountId, string Role, DateTimeOffset JoinedAt);
