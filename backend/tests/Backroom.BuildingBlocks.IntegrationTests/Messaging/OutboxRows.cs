using Npgsql;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;
using Backroom.Testing.Postgres;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

/// <summary>Reads the test module's outbox as superuser.</summary>
public static class OutboxRows
{
    public static async Task<List<OutboxRow>> ReadAsync(PostgresDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT id, org_id, event_type, payload, trace_parent, published_at, failed_at FROM {NotesSchema.Schema}.outbox ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }
}

public sealed record OutboxRow(Guid Id, Guid OrgId, string EventType, ReadOnlyMemory<byte> Payload, string? TraceParent, DateTimeOffset? PublishedAt, DateTimeOffset? FailedAt);
