using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.ControlPlane.Contracts.V1;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// Sends the outbox upstream (docs/contracts/phase-1.md, "Delivery"): due messages in sequence order;
/// <c>Ack</c> or non-retryable <c>Nack</c> settles a message and deletes its payload; a retryable <c>Nack</c>,
/// an overdue answer, or a stream that broke while it was the oldest unanswered message counts an attempt
/// and delays the next send (2 s doubling to 5 min); a message is rejected with <c>delivery_expired</c>
/// instead of its send after <see cref="MaxAttempts"/>, and one that cannot be parsed with <c>invalid_payload</c>. Runs as the relay role (<see cref="DataSourceKey"/>):
/// the application role may only insert into the outbox. Only one worker holds the link (the lease), so
/// in-flight bookkeeping is in memory.
/// </summary>
internal sealed partial class UpstreamDelivery(
    [FromKeyedServices(UpstreamDelivery.DataSourceKey)] NpgsqlDataSource dataSource,
    TimeProvider timeProvider,
    ILogger<UpstreamDelivery> logger)
{
    /// <summary>The keyed service name of the relay role's data source.</summary>
    public const string DataSourceKey = "control-plane-link-relay";

    public const int MaxAttempts = 50;

    public const string DeliveryExpiredCode = "delivery_expired";

    public const string InvalidPayloadCode = "invalid_payload";

    /// <summary>The most payload bytes read for one send batch (at least one message is always read).</summary>
    public const long MaxBatchBytes = 8 * 1024 * 1024;

    private const string Pending = "acked_at IS NULL AND rejected_code IS NULL";

    // In sequence order, or (for a probe, $5) least-tried first, so a message that broke earlier streams
    // does not go first again. The batch stops once the payloads before a row reach the byte cap (at least
    // one row is read).
    private const string Order = "CASE WHEN $5 THEN attempts ELSE 0 END, sequence";

    private const string DueSql = $"""
        SELECT message_id, sequence, attempts, payload, trace_parent, trace_state, type
        FROM (SELECT message_id, sequence, attempts, payload, trace_parent, trace_state, type,
                     sum(octet_length(payload)) OVER (ORDER BY {Order}) - octet_length(payload) AS bytes_before
              FROM (SELECT message_id, sequence, attempts, payload, trace_parent, trace_state, type FROM control_plane_connector.outbox
                    WHERE {Pending} AND next_attempt_at <= $1 AND NOT (message_id = ANY($2))
                    ORDER BY {Order}
                    LIMIT $3) due) batch
        WHERE bytes_before < $4
        ORDER BY {Order}
        """;

    private const string SettleSql = $"""
        UPDATE control_plane_connector.outbox
        SET payload = NULL,
            acked_at = CASE WHEN $2::text IS NULL THEN $3 END,
            rejected_code = $2,
            rejected_at = CASE WHEN $2::text IS NOT NULL THEN $3 END
        WHERE message_id = $1 AND {Pending}
        """;

    // The delay doubles from 2 s up to 5 min (attempts on the right is the value before this update).
    private const string RetrySql = $"""
        UPDATE control_plane_connector.outbox
        SET attempts = attempts + 1,
            next_attempt_at = $2 + make_interval(secs => least(power(2, least(attempts + 1, 16)), 300))
        WHERE message_id = $1 AND {Pending}
        """;

    /// <summary>The due messages to send now, in sequence order; for a probe the least-tried first.</summary>
    public async Task<IReadOnlyList<ConnectRequest>> TakeDueAsync(
        IReadOnlyCollection<Guid> inFlight, int limit, bool probe, CancellationToken cancellationToken)
    {
        var rows = new List<(Guid MessageId, long Sequence, int Attempts, byte[] Payload, string? TraceParent, string? TraceState, string Type)>();
        await using (var command = dataSource.CreateCommand(DueSql))
        {
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
            command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. inFlight] });
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
            command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = MaxBatchBytes });
            command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = probe });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetGuid(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetFieldValue<byte[]>(3),
                    await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetString(4),
                    await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        var messages = new List<ConnectRequest>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Attempts >= MaxAttempts)
            {
                // Nothing retries it after this (an expired InvitationRegistered means no email), so it must be seen.
                LogExpired(logger, row.Type, row.MessageId, MaxAttempts);
                await SettleAsync(row.MessageId, DeliveryExpiredCode, cancellationToken);
                continue;
            }

            ConnectRequest message;
            try
            {
                message = ConnectRequest.Parser.ParseFrom(row.Payload);
            }
            catch (InvalidProtocolBufferException)
            {
                await SettleAsync(row.MessageId, InvalidPayloadCode, cancellationToken);
                continue;
            }

            message.Envelope = new Envelope
            {
                MessageId = row.MessageId.ToString("D"),
                Sequence = (ulong)row.Sequence,
                Traceparent = row.TraceParent ?? string.Empty,
                Tracestate = row.TraceState ?? string.Empty,
            };
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Settles the message: <paramref name="rejectedCode"/> null for an <c>Ack</c>; the payload is deleted.</summary>
    public async Task SettleAsync(Guid messageId, string? rejectedCode, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SettleSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = rejectedCode });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A retryable <c>Nack</c>, an overdue answer or a broken stream: counts an attempt; the next send waits for a backoff.</summary>
    public async Task RetryLaterAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(RetrySql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Link message {Type} {MessageId} was not delivered after {Attempts} attempts and is given up (delivery_expired).")]
    private static partial void LogExpired(ILogger logger, string type, Guid messageId, int attempts);
}
