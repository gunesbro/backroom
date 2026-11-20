using System.Diagnostics;
using Google.Protobuf;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// Writes upstream link messages into <c>control_plane_connector.outbox</c> in the caller's unit of work,
/// so a message is sent if and only if the change that caused it commits. Used by the connector's own
/// event consumers (the connector is the only module that talks to the control plane).
/// </summary>
internal sealed class UpstreamOutbox(DatabaseSession session, TimeProvider timeProvider)
{
    /// <summary>The largest serialized message (a membership snapshot of a large organization), below the stream's 4 MB limit.</summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024 - 1024;

    private const string InsertSql = """
        INSERT INTO control_plane_connector.outbox (message_id, type, payload, trace_parent, trace_state, created_at, next_attempt_at)
        VALUES ($1, $2, $3, $4, $5, $6, $6)
        """;

    /// <summary>Enqueues <paramref name="message"/> (a durable upstream payload, without envelope); returns its message id.</summary>
    public async Task<Guid> EnqueueAsync(ConnectRequest message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadCase is ConnectRequest.PayloadOneofCase.None or ConnectRequest.PayloadOneofCase.Hello
            or ConnectRequest.PayloadOneofCase.Heartbeat or ConnectRequest.PayloadOneofCase.Ack or ConnectRequest.PayloadOneofCase.Nack)
        {
            throw new ArgumentException($"{message.PayloadCase} is not a durable upstream message.", nameof(message));
        }

        if (message.Envelope is not null)
        {
            throw new ArgumentException("The outbox sets the envelope.", nameof(message));
        }

        var payload = message.ToByteArray();
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"The message is {payload.Length} bytes; the limit is {MaxPayloadBytes}.", nameof(message));
        }

        // The current span becomes the parent of the control plane's handling.
        var activity = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;
        var messageId = Guid.CreateVersion7(timeProvider.GetUtcNow());
        await using var command = new NpgsqlCommand(InsertSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.PayloadCase.ToString() });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = payload });
        // Bounded: tracestate can come from a caller's header (W3C suggests at most 512 characters).
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = activity?.Id is { Length: 55 } id ? id : null });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = activity?.TraceStateString is { Length: <= 512 } state ? state : null });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken);
        return messageId;
    }
}
