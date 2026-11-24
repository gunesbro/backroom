using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// A conversation was created (docs/contracts/phase-2.md, "Integration events"). Ids and the kind only, never a name.
/// <paramref name="Kind"/> is one of <see cref="ConversationKinds"/>.
/// </summary>
public sealed record ConversationCreated(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    string Kind,
    Guid CreatedBy) : IIntegrationEvent
{
    public static string EventType => "messaging.conversation-created.v1";
}
