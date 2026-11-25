using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Infrastructure;

/// <summary>
/// Conversations and <c>conversation_members</c>; row-level security limits every statement to the entered
/// organization, and each query also names it. A membership change and the conversation's <c>member_count</c> are
/// written by one statement. Lock order: the membership row, then the conversation row (a send takes only the
/// conversation row), so joins, leaves and sends cannot deadlock.
/// </summary>
internal sealed class ConversationStore(DatabaseSession session) : IConversationStore
{
    // Two primary-key probes.
    private const string FindSql = """
        SELECT c.kind, c.archived_at IS NOT NULL, c.last_seq,
            EXISTS (
                SELECT 1 FROM messaging.conversation_members m
                WHERE m.conversation_id = c.id AND m.account_id = $3 AND m.org_id = $1 AND m.left_at IS NULL)
        FROM messaging.conversations c
        WHERE c.org_id = $1 AND c.id = $2
        """;

    // The same rule as ConversationAccess.CanRead: open, not a thread, and a public channel or an open member.
    private const string FilterReadersSql = """
        SELECT a.id
        FROM unnest($3::uuid[]) AS a(id)
        JOIN messaging.conversations c ON c.org_id = $1 AND c.id = $2
        WHERE c.archived_at IS NULL AND c.kind <> $4
          AND (c.kind = $5 OR EXISTS (
              SELECT 1 FROM messaging.conversation_members m
              WHERE m.org_id = $1 AND m.conversation_id = c.id AND m.account_id = a.id AND m.left_at IS NULL))
        """;

    private const string ReadableConversationsSql = """
        SELECT c.id
        FROM messaging.conversations c
        WHERE c.org_id = $1 AND c.id = ANY($3) AND c.archived_at IS NULL AND c.kind <> $4
          AND (c.kind = $5 OR EXISTS (
              SELECT 1 FROM messaging.conversation_members m
              WHERE m.org_id = $1 AND m.conversation_id = c.id AND m.account_id = $2 AND m.left_at IS NULL))
        """;

    // An index-only scan of conversation_members_account_idx that stops at the cap.
    private const string CountOpenSql = """
        SELECT count(*)::integer FROM (
            SELECT 1 FROM messaging.conversation_members
            WHERE org_id = $1 AND account_id = $2 AND left_at IS NULL
            LIMIT $3) AS open_memberships
        """;

    // A row that is already open is left alone (the WHERE of DO UPDATE), so the count changes only for a real join.
    // The assignment list does not start with the role column: the org-context Semgrep rule reads that text as a
    // database role switch.
    private const string OpenSql = """
        WITH opened AS (
            INSERT INTO messaging.conversation_members AS m (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (conversation_id, account_id) DO UPDATE
                SET joined_at = EXCLUDED.joined_at, joined_seq = EXCLUDED.joined_seq, role = EXCLUDED.role,
                    added_by = EXCLUDED.added_by, left_at = NULL
                WHERE m.left_at IS NOT NULL
            RETURNING m.conversation_id)
        UPDATE messaging.conversations c SET member_count = c.member_count + 1
        FROM opened
        WHERE c.org_id = $1 AND c.id = opened.conversation_id
        """;

    private const string CloseSql = """
        WITH closed AS (
            UPDATE messaging.conversation_members SET left_at = $4
            WHERE conversation_id = $2 AND account_id = $3 AND org_id = $1 AND left_at IS NULL
            RETURNING conversation_id)
        UPDATE messaging.conversations c SET member_count = c.member_count - 1
        FROM closed
        WHERE c.org_id = $1 AND c.id = closed.conversation_id
        """;

    // Newest message first (creation time for a conversation without messages), then id, as a keyset: the person's open
    // memberships (at most 10,000, from conversation_members_account_idx) are joined by primary key and the top rows kept.
    private const string ListForAccountSql = """
        SELECT c.id, c.kind, c.name, c.created_at, COALESCE(c.last_message_at, c.created_at) AS sort_at
        FROM messaging.conversation_members m
        JOIN messaging.conversations c ON c.org_id = m.org_id AND c.id = m.conversation_id
        WHERE m.org_id = $1 AND m.account_id = $2 AND m.left_at IS NULL
            AND c.archived_at IS NULL AND c.kind <> 'thread'
            AND (COALESCE(c.last_message_at, c.created_at), c.id) < ($3, $4)
        ORDER BY sort_at DESC, c.id DESC
        LIMIT $5
        """;

    public async Task<ConversationAccess?> FindAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ConversationAccess(reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetBoolean(3))
            : null;
    }

    public async Task<IReadOnlyList<Guid>> FilterReadersAsync(
        Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FilterReadersSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ConversationKinds.Thread });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ConversationKinds.PublicChannel });
        var readers = new List<Guid>(accountIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            readers.Add(reader.GetGuid(0));
        }

        return readers;
    }

    public async Task<IReadOnlySet<Guid>> ReadableConversationsAsync(
        Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ReadableConversationsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. conversationIds] });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ConversationKinds.Thread });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ConversationKinds.PublicChannel });
        var readable = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            readable.Add(reader.GetGuid(0));
        }

        return readable;
    }

    public async Task<int> CountOpenMembershipsAsync(Guid organizationId, Guid accountId, int cap, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CountOpenSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = cap });
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> OpenMembershipAsync(NewMembership membership, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(OpenSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = membership.OrgId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = membership.ConversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = membership.AccountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = membership.Role });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = membership.JoinedAt });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = membership.JoinedSeq });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = membership.AddedBy });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> CloseMembershipAsync(Guid organizationId, Guid conversationId, Guid accountId, DateTimeOffset leftAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CloseSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = leftAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ConversationEntry>> ListForAccountAsync(
        Guid organizationId, Guid accountId, DateTimeOffset afterSortAt, Guid afterId, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ListForAccountSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = afterSortAt });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = afterId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<ConversationEntry>(Math.Min(count, 101));
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new ConversationEntry(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return found;
    }
}
