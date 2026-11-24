using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// An account became an open member of a conversation: it joined (<paramref name="ActorAccountId"/> is itself), was
/// added, or created the conversation (docs/contracts/phase-2.md, "Integration events"). Ids only.
/// </summary>
public sealed record ConversationMemberAdded(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid AccountId,
    Guid ActorAccountId) : IIntegrationEvent
{
    public static string EventType => "messaging.conversation-member-added.v1";
}
