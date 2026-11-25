using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// A channel was renamed, archived or unarchived, or a private channel's ownership moved
/// (docs/contracts/phase-2.md, "Integration events"). Ids, the kind, the change and a number only: never a name, so the
/// audit log can record private channels without learning what they are called.
/// </summary>
/// <param name="Kind">One of <see cref="ConversationKinds"/>.</param>
/// <param name="Change">One of <see cref="ConversationChanges"/>.</param>
/// <param name="MemberCount">The conversation's open memberships after the change.</param>
public sealed record ConversationChanged(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    string Kind,
    string Change,
    Guid ActorAccountId,
    int MemberCount) : IIntegrationEvent
{
    public static string EventType => "messaging.conversation-changed.v1";
}
