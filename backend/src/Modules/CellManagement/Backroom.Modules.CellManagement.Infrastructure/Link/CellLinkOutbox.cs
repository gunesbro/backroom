using System.Diagnostics;
using Google.Protobuf;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.CellManagement.Infrastructure.Link;

/// <summary>Writes downstream link messages into <c>cell_management.outbound_messages</c> in the caller's unit of work.</summary>
internal sealed class CellLinkOutbox(DatabaseSession session, TimeProvider timeProvider) : ICellLinkOutbox
{
    private const string InsertSql = """
        INSERT INTO cell_management.outbound_messages
            (message_id, cell_id, type, payload, trace_parent, trace_state, created_at, next_attempt_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $7)
        """;

    public async Task<Guid> EnqueueAsync(string cellId, ConnectResponse message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        ArgumentNullException.ThrowIfNull(message);
        if (message.PayloadCase is ConnectResponse.PayloadOneofCase.None or ConnectResponse.PayloadOneofCase.Welcome
            or ConnectResponse.PayloadOneofCase.Ack or ConnectResponse.PayloadOneofCase.Nack)
        {
            throw new ArgumentException($"{message.PayloadCase} is not a durable downstream message.", nameof(message));
        }

        if (message.Envelope is not null)
        {
            throw new ArgumentException("The outbox sets the envelope.", nameof(message));
        }

        var payload = message.ToByteArray();
        if (payload.Length > ICellLinkOutbox.MaxPayloadBytes)
        {
            throw new ArgumentException($"The message is {payload.Length} bytes; the limit is {ICellLinkOutbox.MaxPayloadBytes}.", nameof(message));
        }

        // The current span becomes the parent of the cell's handling.
        var activity = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;
        var messageId = Guid.CreateVersion7(timeProvider.GetUtcNow());
        await using var command = new NpgsqlCommand(InsertSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.PayloadCase.ToString() });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = payload });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = activity?.Id });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = activity?.TraceStateString });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken);
        return messageId;
    }
}
