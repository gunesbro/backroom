using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Application.Link;

namespace Backroom.Modules.CellManagement.Infrastructure.Link;

/// <summary>
/// <c>cell_management.outbound_messages</c> with hand-written SQL: leasing runs on its own connection,
/// settling in the scope's unit of work. Every statement filters by the cell, so a cell can settle only its
/// own messages.
/// </summary>
internal sealed class OutboundMessages(NpgsqlDataSource dataSource, DatabaseSession session) : IOutboundMessages
{
    private const string Columns = "message_id, sequence, attempts, cell_answer, payload, trace_parent, trace_state";
    private const string Pending = "acked_at IS NULL AND rejected_code IS NULL";

    // One statement; rows being settled are skipped. Stream ids are UUIDv7, so only a newer stream takes
    // over an older stream's lease (two live streams never take turns). Settling and retrying clear the
    // lease, so a lease still set means a send without an answer: taking it over counts an attempt.
    private const string LeaseSql = $"""
        UPDATE cell_management.outbound_messages m
        SET leased_until = $4,
            leased_by = $2,
            attempts = m.attempts + CASE WHEN m.leased_until IS NOT NULL THEN 1 ELSE 0 END
        FROM (SELECT message_id FROM cell_management.outbound_messages
              WHERE cell_id = $1 AND {Pending} AND next_attempt_at <= $3
                AND (leased_until IS NULL OR leased_until <= $3 OR leased_by < $2)
              ORDER BY sequence
              LIMIT $5
              FOR UPDATE SKIP LOCKED) due
        WHERE m.message_id = due.message_id
        RETURNING m.message_id, m.sequence, m.attempts, m.cell_answer, m.payload, m.trace_parent, m.trace_state
        """;

    private const string LockSql = $"""
        SELECT {Columns} FROM cell_management.outbound_messages
        WHERE message_id = $1 AND cell_id = $2 AND {Pending}
        FOR UPDATE
        """;

    // The payload is deleted once settled (it may carry a name and an email).
    private const string SettleSql = $"""
        UPDATE cell_management.outbound_messages
        SET payload = NULL,
            leased_until = NULL,
            leased_by = NULL,
            acked_at = CASE WHEN $3::text IS NULL THEN $4 END,
            rejected_code = $3,
            rejected_at = CASE WHEN $3::text IS NOT NULL THEN $4 END
        WHERE message_id = $1 AND cell_id = $2 AND {Pending}
        """;

    // The delay doubles from 2 s up to 5 min (attempts on the right is the value before this update).
    private const string RetrySql = $"""
        UPDATE cell_management.outbound_messages
        SET attempts = attempts + 1,
            leased_until = NULL,
            leased_by = NULL,
            cell_answer = coalesce(cell_answer, $4),
            next_attempt_at = $3 + make_interval(secs => least(power(2, least(attempts + 1, 16)), 300))
        WHERE message_id = $1 AND cell_id = $2 AND {Pending}
        RETURNING attempts, cell_answer
        """;

    public async Task<IReadOnlyList<OutboundMessage>> LeaseDueAsync(
        string cellId, Guid streamId, DateTimeOffset now, DateTimeOffset leaseUntil, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(LeaseSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = streamId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = leaseUntil });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<OutboundMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(await ReadAsync(reader, cancellationToken));
        }

        // RETURNING has no order.
        messages.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        return messages;
    }

    public async Task<OutboundMessage?> LockPendingAsync(string cellId, Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LockSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? await ReadAsync(reader, cancellationToken) : null;
    }

    public async Task SettleAsync(string cellId, Guid messageId, string? rejectedCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SettleSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = rejectedCode });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(int Attempts, string? CellAnswer)?> ScheduleRetryAsync(
        string cellId, Guid messageId, string? cellAnswer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RetrySql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = cellAnswer });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt32(0), await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1))
            : null;
    }

    private static async Task<OutboundMessage> ReadAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var message = ConnectResponse.Parser.ParseFrom(reader.GetFieldValue<byte[]>(4));
        message.Envelope = new Envelope
        {
            MessageId = reader.GetGuid(0).ToString("D"),
            Sequence = (ulong)reader.GetInt64(1),
            Traceparent = await reader.IsDBNullAsync(5, cancellationToken) ? string.Empty : reader.GetString(5),
            Tracestate = await reader.IsDBNullAsync(6, cancellationToken) ? string.Empty : reader.GetString(6),
        };
        var cellAnswer = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
        return new OutboundMessage(reader.GetGuid(0), reader.GetInt64(1), reader.GetInt32(2), cellAnswer, message);
    }
}
