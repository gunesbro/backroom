using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

/// <summary>Writes a note for the event, then fails if the probe says so (so the write must be rolled back).</summary>
public sealed class NoteAddedHandler(NotesDbContext notes, IOrganizationContext organizationContext, HandlerProbe probe)
    : IIntegrationEventHandler<NoteAdded>
{
    public async Task HandleAsync(NoteAdded integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        probe.Record(integrationEvent.EventId, organizationContext.OrganizationId);

        notes.Notes.Add(new Note { Id = integrationEvent.NoteId, OrganizationId = integrationEvent.OrgId, Text = "handled" });
        await notes.SaveChangesAsync(cancellationToken);

        if (probe.ShouldFail(integrationEvent.EventId))
        {
            throw new InvalidOperationException("Injected failure.");
        }
    }
}
