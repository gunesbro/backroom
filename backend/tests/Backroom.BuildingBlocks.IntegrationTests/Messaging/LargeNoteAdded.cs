using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

/// <summary>An event that carries content, which real events must not; used to test the payload limit.</summary>
public sealed record LargeNoteAdded(Guid EventId, Guid OrgId, DateTimeOffset OccurredAt, string Text) : IIntegrationEvent
{
    public static string EventType => "rls_test.large-note-added.v1";
}
