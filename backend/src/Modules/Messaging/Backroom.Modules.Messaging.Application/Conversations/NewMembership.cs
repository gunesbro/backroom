namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>An open membership to add, or to reopen if the account left before.</summary>
internal sealed record NewMembership(
    Guid OrgId,
    Guid ConversationId,
    Guid AccountId,
    string Role,
    DateTimeOffset JoinedAt,
    long JoinedSeq,
    Guid AddedBy);
