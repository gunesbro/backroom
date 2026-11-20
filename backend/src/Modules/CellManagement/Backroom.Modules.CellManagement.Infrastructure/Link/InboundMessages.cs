using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.CellManagement.Application.Link;

namespace Backroom.Modules.CellManagement.Infrastructure.Link;

/// <summary><c>cell_management.inbound_messages</c> in the scope's unit of work.</summary>
internal sealed class InboundMessages(DatabaseSession session) : IInboundMessages
{
    private const string RecordSql = """
        INSERT INTO cell_management.inbound_messages (message_id, cell_id, type, received_at)
        VALUES ($1, $2, $3, $4)
        ON CONFLICT (cell_id, message_id) DO NOTHING
        """;

    public async Task<bool> TryRecordAsync(Guid messageId, string cellId, string type, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RecordSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = type });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
