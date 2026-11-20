using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Events;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Writes events into the publishing module's outbox table inside the active unit of work.</summary>
internal sealed class Outbox(DatabaseSession session, IOrganizationContext organizationContext) : IOutbox
{
    [SuppressMessage("Security", "CA2100", Justification = "Only the table name is formatted in, from a schema validated as a snake_case identifier and quoted; values are parameters.")]
    public async Task AddAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (integrationEvent.EventId == Guid.Empty)
        {
            throw new ArgumentException("The event id must not be empty.", nameof(integrationEvent));
        }

        // Row-level security would reject it too; this gives a clear error first.
        if (integrationEvent.OrgId != organizationContext.OrganizationId)
        {
            throw new InvalidOperationException("The event belongs to a different organization than the unit of work.");
        }

        var payload = IntegrationEventSerializer.Serialize(integrationEvent);
        if (payload.Length > OutboxTable.MaxPayloadBytes)
        {
            throw new ArgumentException(
                $"The serialized event is {payload.Length} bytes; events carry ids, and the limit is {OutboxTable.MaxPayloadBytes} bytes.",
                nameof(integrationEvent));
        }

        // The current span becomes the parent of the publish and consume spans.
        var activity = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;

        await using var command = new NpgsqlCommand(OutboxInsert<TEvent>.Sql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = integrationEvent.EventId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = integrationEvent.OrgId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = IntegrationEventMetadata<TEvent>.EventType });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = payload });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = activity?.Id });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = activity?.TraceStateString });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = integrationEvent.OccurredAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static class OutboxInsert<TEvent>
        where TEvent : IIntegrationEvent
    {
        public static readonly string Sql =
            $"INSERT INTO {OrganizationRowLevelSecurity.QualifiedName(IntegrationEventMetadata<TEvent>.Module, OutboxTable.Name)} " +
            "(id, org_id, event_type, payload, trace_parent, trace_state, occurred_at) VALUES ($1, $2, $3, $4, $5, $6, $7)";
    }
}
