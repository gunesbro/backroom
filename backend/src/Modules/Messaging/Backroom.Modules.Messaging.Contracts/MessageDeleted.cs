using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// A message was deleted and is now a tombstone (docs/contracts/phase-2.md, "Integration events"). Ids only.
/// <paramref name="RemovedBy"/> is the moderator's account when someone other than the author deleted it, otherwise
/// <see langword="null"/>.
/// </summary>
public sealed record MessageDeleted(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid MessageId,
    Guid? RemovedBy) : IIntegrationEvent
{
    public static string EventType => "messaging.message-deleted.v1";
}
