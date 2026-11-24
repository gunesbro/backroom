using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// An account's membership of a conversation ended: it left (<paramref name="ActorAccountId"/> is itself) or was
/// removed (docs/contracts/phase-2.md, "Integration events"). Read access ends with the commit; Realtime's consumer
/// (Phase 2 step 2) removes the account's connections from the conversation's group. Ids only.
/// </summary>
public sealed record ConversationMemberRemoved(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid AccountId,
    Guid ActorAccountId) : IIntegrationEvent
{
    public static string EventType => "messaging.conversation-member-removed.v1";
}
