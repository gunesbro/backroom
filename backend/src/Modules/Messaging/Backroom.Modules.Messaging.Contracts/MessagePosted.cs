using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// A message was posted in a channel (docs/contracts/phase-1.md, "Integration events"). It carries ids only, never the
/// text: search and notifications read the message through their own permission check.
/// </summary>
public sealed record MessagePosted(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ChannelId,
    Guid MessageId,
    Guid AuthorAccountId,
    DateTimeOffset CreatedAt) : IIntegrationEvent
{
    public static string EventType => "messaging.message-posted.v1";
}
