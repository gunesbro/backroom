using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Messages;

namespace Backroom.Modules.Messaging.Infrastructure;

/// <summary>Messages; row-level security limits every statement to the entered organization.</summary>
internal sealed class MessageStore(DatabaseSession session) : IMessageStore
{
    // The quote of a reply comes with the page: one LEFT JOIN by primary key on the original (at most one row per message of the page, no
    // second statement, no N+1). q.id is null when the original row is gone, which reads as a deleted original. left() bounds what is read
    // (code points; the quote is then cut to its length in characters).
    private const string Columns = "m.id, m.seq, m.version_seq, m.client_message_id, m.author_account_id, m.text, m.created_at, m.deleted_at, m.blocks, m.edited_at, m.reply_to_message_id, q.id, q.deleted_at, q.author_account_id, left(q.text, 400), m.reaction_summary";

    private const string QuoteJoin = "LEFT JOIN messaging.messages q ON q.id = m.reply_to_message_id";

    // A new message's latest change is its creation: version_seq starts as seq. The blocks come back as the database holds them
    // (jsonb keeps no key order or spacing), so the answer to a send is the same as every later read.
    private const string AddSql = """
        INSERT INTO messaging.messages (id, org_id, conversation_id, seq, version_seq, author_account_id, client_message_id, text, blocks, created_at, reply_to_message_id)
        VALUES ($1, $2, $3, $4, $4, $5, $6, $7, $8, $9, $10)
        ON CONFLICT (conversation_id, author_account_id, client_message_id) DO NOTHING
        RETURNING blocks
        """;

    private const string FindSql = $"""
        SELECT {Columns}
        FROM messaging.messages m
        {QuoteJoin}
        WHERE m.conversation_id = $1 AND m.author_account_id = $2 AND m.client_message_id = $3
        """;

    // The anchor of an around read: one row by the primary key, in its conversation.
    private const string FindInConversationSql = $"""
        SELECT {Columns}
        FROM messaging.messages m
        {QuoteJoin}
        WHERE m.conversation_id = $1 AND m.id = $2
        """;

    // History: newest first, by sequence number (commit order), tombstones included (they keep their place). Served by
    // the unique index (conversation_id, seq) read backwards.
    private const string ListBeforeSql = $"""
        SELECT {Columns}
        FROM messaging.messages m
        {QuoteJoin}
        WHERE m.conversation_id = $1 AND m.seq < $2
        ORDER BY m.seq DESC
        LIMIT $3
        """;

    // Forward paging by seq: oldest first.
    private const string ListAfterSql = $"""
        SELECT {Columns}
        FROM messaging.messages m
        {QuoteJoin}
        WHERE m.conversation_id = $1 AND m.seq > $2
        ORDER BY m.seq
        LIMIT $3
        """;

    // Catch-up: everything that changed after the cursor (new and edited messages, tombstones), in the order of the
    // changes. Served by messages_conversation_version_seq_idx.
    private const string ListChangedAfterSql = $"""
        SELECT {Columns}
        FROM messaging.messages m
        {QuoteJoin}
        WHERE m.conversation_id = $1 AND m.version_seq > $2
        ORDER BY m.version_seq
        LIMIT $3
        """;

    // One statement for all the rows of a message (at most 50 accounts).
    private const string AddMentionsSql = """
        INSERT INTO messaging.message_mentions (org_id, message_id, conversation_id, account_id)
        SELECT $1, $2, $3, account_id FROM unnest($4::uuid[]) AS account_id
        """;

    // One statement for all the rows of a message; the position keeps their order in the message.
    private const string AddLinksSql = """
        INSERT INTO messaging.message_links (org_id, conversation_id, message_id, position, url, host, created_at)
        SELECT $1, $2, $3, link.position, link.url, link.host, $6
        FROM unnest($4::text[], $5::text[]) WITH ORDINALITY AS link(url, host, position)
        """;

    private const string HeadSql = "SELECT conversation_id, author_account_id, deleted_at IS NOT NULL FROM messaging.messages WHERE id = $1";

    private const string QuoteSourceSql = "SELECT conversation_id, author_account_id, deleted_at IS NOT NULL, left(text, 400) FROM messaging.messages WHERE id = $1";

    // The content and the version change together; a deleted message is never edited (the check constraint would refuse it too).
    // The update runs in a CTE so the answer carries the quote like every read (the quoted original is another row, unchanged here).
    private const string EditSql = $"""
        WITH m AS (
            UPDATE messaging.messages
            SET text = $2, blocks = $3, version_seq = $4, edited_at = $5
            WHERE id = $1 AND deleted_at IS NULL
            RETURNING *)
        SELECT {Columns}
        FROM m
        {QuoteJoin}
        """;

    private const string DeleteMentionsSql = "DELETE FROM messaging.message_mentions WHERE message_id = $1 RETURNING account_id";

    private const string DeleteLinksSql = "DELETE FROM messaging.message_links WHERE message_id = $1";

    // One statement: the tombstone and the erasure of everything derived from the content. The data-modifying CTEs run
    // whether or not the update finds a live row; an already deleted message has no rows in them anyway.
    private const string DeleteSql = """
        WITH erased_mentions AS (DELETE FROM messaging.message_mentions WHERE message_id = $1),
             erased_links AS (DELETE FROM messaging.message_links WHERE message_id = $1),
             erased_reactions AS (DELETE FROM messaging.reactions WHERE message_id = $1)
        UPDATE messaging.messages
        SET text = NULL, blocks = NULL, edited_at = NULL, reply_to_message_id = NULL, reaction_summary = NULL, version_seq = $2, deleted_at = $3
        WHERE id = $1 AND deleted_at IS NULL
        """;

    // The limits and the insert in one statement, under the conversation row lock the caller holds. "mine" counts the person's reactions on the
    // message (primary key prefix); "msg" reads the message's stored summary, whose length is the number of distinct emoji and which names the
    // emoji it holds. 0 added, 1 already there, 2 person limit, 3 emoji limit.
    private const string AddReactionSql = """
        WITH mine AS (
            SELECT count(*)::int AS total, coalesce(bool_or(emoji = $4), false) AS has
            FROM messaging.reactions
            WHERE message_id = $2 AND account_id = $3),
        msg AS (
            SELECT coalesce(jsonb_array_length(reaction_summary), 0) AS distinct_emoji,
                   coalesce(reaction_summary @> jsonb_build_array(jsonb_build_object('emoji', $4::text)), false) AS known
            FROM messaging.messages
            WHERE id = $2),
        ins AS (
            INSERT INTO messaging.reactions (org_id, message_id, account_id, emoji, created_at)
            SELECT $1, $2, $3, $4, $5
            FROM mine, msg
            WHERE NOT mine.has AND mine.total < 20 AND (msg.known OR msg.distinct_emoji < 50)
            RETURNING 1)
        SELECT CASE WHEN EXISTS (SELECT 1 FROM ins) THEN 0 WHEN mine.has THEN 1 WHEN mine.total >= 20 THEN 2 ELSE 3 END
        FROM mine
        """;

    private const string RemoveReactionSql = "DELETE FROM messaging.reactions WHERE message_id = $1 AND account_id = $2 AND emoji = $3";

    // One aggregate over the rows of one message (primary key prefix, at most people x 20 rows): count and the earliest three reactors per
    // emoji, emoji in the order of their first reaction (then by code point). No row left gives NULL.
    private const string RefreshReactionSummarySql = """
        UPDATE messaging.messages
        SET version_seq = $2,
            reaction_summary = (
                SELECT jsonb_agg(jsonb_build_object('emoji', r.emoji, 'count', r.total, 'sample', to_jsonb(r.sample)) ORDER BY r.first_at, r.emoji COLLATE "C")
                FROM (
                    SELECT emoji, count(*)::int AS total, min(created_at) AS first_at, (array_agg(account_id ORDER BY created_at, account_id))[1:3] AS sample
                    FROM messaging.reactions
                    WHERE message_id = $1
                    GROUP BY emoji) r)
        WHERE id = $1 AND deleted_at IS NULL
        RETURNING reaction_summary
        """;

    // reactedByMe for a page: the primary key (message_id, account_id, emoji) serves the array of message ids and the account.
    private const string ListReactedBySql = "SELECT message_id, emoji FROM messaging.reactions WHERE message_id = ANY($1) AND account_id = $2";

    private const string PreviewSql = """
        SELECT id, conversation_id, text
        FROM messaging.messages
        WHERE id = ANY($1) AND deleted_at IS NULL
        """;

    public async Task<string?> AddAsync(NewMessage message, CancellationToken cancellationToken)
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
        command.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = message.ReplyToMessageId });
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task AddMentionsAsync(Guid organizationId, Guid conversationId, Guid messageId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        await using var command = new NpgsqlCommand(AddMentionsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddLinksAsync(Guid organizationId, Guid conversationId, Guid messageId, IReadOnlyList<MessageLink> links, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(links);
        var urls = new string[links.Count];
        var hosts = new string[links.Count];
        for (var index = 0; index < links.Count; index++)
        {
            urls[index] = links[index].Url;
            hosts[index] = links[index].Host;
        }

        await using var command = new NpgsqlCommand(AddLinksSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = urls });
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = hosts });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = createdAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<MessageEntry?> FindInConversationAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindInConversationSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
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

    public async Task<MessageHead?> FindHeadAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(HeadSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MessageHead(messageId, reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2))
            : null;
    }

    public async Task<QuoteSource?> FindQuoteSourceAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(QuoteSourceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var deleted = reader.GetBoolean(2);
        return new QuoteSource(reader.GetGuid(0), reader.GetGuid(1), deleted, deleted || reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public async Task<MessageEntry?> EditAsync(Guid messageId, long versionSeq, string text, string blocks, DateTimeOffset editedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EditSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = text });
        command.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Jsonb, TypedValue = blocks });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = versionSeq });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = editedAt });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<Guid>> DeleteMentionsAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DeleteMentionsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accounts = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            accounts.Add(reader.GetGuid(0));
        }

        return accounts;
    }

    public async Task DeleteLinksAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DeleteLinksSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid messageId, long versionSeq, DateTimeOffset deletedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DeleteSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = versionSeq });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = deletedAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<ReactionAdd> AddReactionAsync(Guid organizationId, Guid messageId, Guid accountId, string emoji, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AddReactionSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = emoji });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = createdAt });
        return (ReactionAdd)(int)(await command.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("The reaction statement returned no row."));
    }

    public async Task<bool> RemoveReactionAsync(Guid messageId, Guid accountId, string emoji, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RemoveReactionSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = emoji });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ReactionSummary>> RefreshReactionSummaryAsync(Guid messageId, long versionSeq, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RefreshReactionSummarySql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = versionSeq });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0) ? ParseSummary(reader.GetString(0)) : [];
    }

    public async Task<IReadOnlyList<(Guid MessageId, string Emoji)>> ListReactedByAsync(IReadOnlyCollection<Guid> messageIds, Guid accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        await using var command = new NpgsqlCommand(ListReactedBySql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. messageIds] });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<(Guid, string)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return found;
    }

    // The stored summary: [{"emoji", "count", "sample": [account ids]}]; reactedByMe is the reader's and is filled by the service.
    private static List<ReactionSummary> ParseSummary(string json)
    {
        using var document = JsonDocument.Parse(json);
        var summary = new List<ReactionSummary>(document.RootElement.GetArrayLength());
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var sample = new List<Guid>(ReactionSummary.MaxSample);
            foreach (var account in item.GetProperty("sample").EnumerateArray())
            {
                sample.Add(account.GetGuid());
            }

            summary.Add(new ReactionSummary(item.GetProperty("emoji").GetString()!, item.GetProperty("count").GetInt32(), ReactedByMe: false, sample));
        }

        return summary;
    }

    public async Task<IReadOnlyList<MessagePreviewSource>> ListForPreviewAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        await using var command = new NpgsqlCommand(PreviewSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. messageIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<MessagePreviewSource>(messageIds.Count);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new MessagePreviewSource(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
        }

        return found;
    }

    // A reply (reply_to_message_id set) quotes its original: a live one with author and text, a deleted one or one whose row is gone with its id only.
    private static MessageQuote? ReadQuote(NpgsqlDataReader reader)
    {
        if (reader.IsDBNull(10))
        {
            return null;
        }

        var originalId = reader.GetGuid(10);
        return reader.IsDBNull(11) || !reader.IsDBNull(12) || reader.IsDBNull(14)
            ? MessageQuote.OfDeleted(originalId)
            : MessageQuote.Of(originalId, reader.GetGuid(13), reader.GetString(14));
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
            deletedAt is null && !reader.IsDBNull(8) ? reader.GetString(8) : null,
            reader.GetFieldValue<DateTimeOffset>(6),
            deletedAt,
            deletedAt is null && !reader.IsDBNull(9) ? reader.GetFieldValue<DateTimeOffset>(9) : null,
            deletedAt is null ? ReadQuote(reader) : null,
            deletedAt is null ? (reader.IsDBNull(15) ? [] : ParseSummary(reader.GetString(15))) : null);
    }
}
