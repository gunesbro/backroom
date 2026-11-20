using Backroom.BuildingBlocks.Domain.Events;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

/// <summary>An event of the test module whose schema is <see cref="NotesSchema.Schema"/>.</summary>
public sealed record NoteAdded(Guid EventId, Guid OrgId, DateTimeOffset OccurredAt, Guid NoteId) : IIntegrationEvent
{
    public static string EventType => "rls_test.note-added.v1";

    public static NoteAdded For(Guid organizationId) =>
        new(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, Guid.CreateVersion7());
}
