using System.Diagnostics.CodeAnalysis;
using Npgsql;
using NpgsqlTypes;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Messages;

namespace Backroom.Modules.Messaging.Infrastructure;

/// <summary>Messages; row-level security limits every statement to the entered organization.</summary>
internal sealed class MessageStore(DatabaseSession session) : IMessageStore
{
    private const string Columns = "id, seq, version_seq, client_message_id, author_account_id, text, created_at, deleted_at";

    // A new message's latest change is its creation: version_seq starts as seq.
    private const string AddSql = """
        INSERT INTO messaging.messages (id, org_id, conversation_id, seq, version_seq, author_account_id, client_message_id, text, blocks, created_at)
        VALUES ($1, $2, $3, $4, $4, $5, $6, $7, $8, $9)
        ON CONFLICT (conversation_id, author_account_id, client_message_id) DO NOTHING
        """;

    private const string FindSql = $"""
        SELECT {Columns}
        FROM messaging.messages
        WHERE conversation_id = $1 AND author_account_id = $2 AND client_message_id = $3
        """;

    // History: newest first, by sequence number (commit order), tombstones included (they keep their place). Served by
    // the unique index (conversation_id, seq) read backwards.
    private const string ListBeforeSql = $"""
        SELECT {Columns}
        FROM messaging.messages
        WHERE conversation_id = $1 AND seq < $2
        ORDER BY seq DESC
        LIMIT $3
        """;

    // Forward paging by seq: oldest first.
    private const string ListAfterSql = $"""
        SELECT {Columns}
        FROM messaging.messages
        WHERE conversation_id = $1 AND seq > $2
        ORDER BY seq
        LIMIT $3
        """;

    // Catch-up: everything that changed after the cursor (new and edited messages, tombstones), in the order of the
    // changes. Served by messages_conversation_version_seq_idx.
    private const string ListChangedAfterSql = $"""
        SELECT {Columns}
        FROM messaging.messages
        WHERE conversation_id = $1 AND version_seq > $2
        ORDER BY version_seq
        LIMIT $3
        """;

    public async Task<bool> AddAsync(NewMessage message, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AddSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.Id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.OrgId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.ChannelId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = message.Seq });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.AuthorAccountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = message.ClientMessageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = message.Text });
        command.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = message.Blocks });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = message.CreatedAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<MessageEntry?> FindAsync(Guid channelId, Guid authorAccountId, Guid clientMessageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channelId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = authorAccountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientMessageId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task<IReadOnlyList<MessageEntry>> ListBeforeAsync(Guid channelId, long before, int count, CancellationToken cancellationToken) =>
        ListAsync(ListBeforeSql, channelId, before, count, cancellationToken);

    public Task<IReadOnlyList<MessageEntry>> ListAfterAsync(Guid channelId, long after, int count, CancellationToken cancellationToken) =>
        ListAsync(ListAfterSql, channelId, after, count, cancellationToken);

    public Task<IReadOnlyList<MessageEntry>> ListChangedAfterAsync(Guid conversationId, long changedAfter, int count, CancellationToken cancellationToken) =>
        ListAsync(ListChangedAfterSql, conversationId, changedAfter, count, cancellationToken);

    [SuppressMessage("Security", "CA2100", Justification = "Only this class constant SQL statements are passed in; values are parameters.")]
    private async Task<IReadOnlyList<MessageEntry>> ListAsync(string sql, Guid channelId, long cursor, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channelId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cursor });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<MessageEntry>(Math.Min(count, 101));
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(Read(reader));
        }

        return messages;
    }

    // A tombstone never carries content, whatever the row holds (the check constraint keeps it empty as well).
    private static MessageEntry Read(NpgsqlDataReader reader)
    {
        DateTimeOffset? deletedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7);
        return new MessageEntry(
            reader.GetGuid(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            deletedAt is null && !reader.IsDBNull(5) ? reader.GetString(5) : null,
            reader.GetFieldValue<DateTimeOffset>(6),
            deletedAt);
    }
}
