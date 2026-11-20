namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>A deserialized event with the ids the message envelope is checked against.</summary>
internal sealed record ReceivedEvent(object Event, Guid EventId, Guid OrgId);
