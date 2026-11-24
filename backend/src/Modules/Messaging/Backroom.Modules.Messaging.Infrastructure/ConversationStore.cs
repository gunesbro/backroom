using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Conversations;

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
}
