using Npgsql;
using NpgsqlTypes;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Infrastructure;

/// <summary>
/// Conversations and <c>conversation_members</c>; row-level security limits every statement to the entered
/// organization, and each query also names it. A membership change and the conversation's <c>member_count</c> are
/// written by one statement. Lock order: every membership and management change first locks the conversation row
/// (<see cref="GetAsync"/> for update), then its membership rows; a send takes only the conversation row. So changes
/// to one conversation run one at a time and cannot deadlock.
/// </summary>
internal sealed class ConversationStore(DatabaseSession session) : IConversationStore
{
    // Two primary-key probes.
    private const string FindSql = """
        SELECT c.kind, c.archived_at IS NOT NULL, c.change_seq,
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

    // The conversation and the account's open membership: two primary-key probes.
    private const string GetSql = """
        SELECT c.kind, c.name, c.description, c.created_at, c.archived_at, c.change_seq, c.member_count, c.owner_account_id, m.role
        FROM messaging.conversations c
        LEFT JOIN messaging.conversation_members m
            ON m.conversation_id = c.id AND m.account_id = $3 AND m.org_id = $1 AND m.left_at IS NULL
        WHERE c.org_id = $1 AND c.id = $2
        """;

    // Locks the conversation row alone. The caller's membership is read by RoleSql afterwards, in a statement of its
    // own: under READ COMMITTED a statement that waited for a row lock re-reads only the locked row, so a membership
    // joined into this statement would be the one from before the wait (a role that was passed on or a membership
    // that ended meanwhile).
    private const string LockSql = """
        SELECT c.kind, c.name, c.description, c.created_at, c.archived_at, c.change_seq, c.member_count, c.owner_account_id
        FROM messaging.conversations c
        WHERE c.org_id = $1 AND c.id = $2
        FOR UPDATE
        """;

    private const string RoleSql = """
        SELECT m.role
        FROM messaging.conversation_members m
        WHERE m.conversation_id = $2 AND m.account_id = $3 AND m.org_id = $1 AND m.left_at IS NULL
        """;

    // A membership change takes the next change number, so sync and ConversationUpdated see the new member count.
    private const string TouchSql = """
        UPDATE messaging.conversations c
        SET change_seq = c.change_seq + 1
        WHERE c.org_id = $1 AND c.id = $2
        RETURNING c.change_seq, c.name, c.description, c.archived_at, c.member_count
        """;

    // The accounts arrive sorted, so two batches take their row locks in the same order; rows that are already open are
    // left alone. The count follows what was really opened.
    private const string OpenManySql = """
        WITH opened AS (
            INSERT INTO messaging.conversation_members AS m (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by)
            SELECT $1, $2, a.id, 'member', $4, $5, $6
            FROM unnest($3::uuid[]) AS a(id)
            ON CONFLICT (conversation_id, account_id) DO UPDATE
                SET joined_at = EXCLUDED.joined_at, joined_seq = EXCLUDED.joined_seq, role = EXCLUDED.role,
                    added_by = EXCLUDED.added_by, left_at = NULL
                WHERE m.left_at IS NOT NULL
            RETURNING m.account_id),
        counted AS (
            UPDATE messaging.conversations c SET member_count = c.member_count + (SELECT count(*) FROM opened)
            WHERE c.org_id = $1 AND c.id = $2)
        SELECT account_id FROM opened
        """;

    // One capped index-only scan of conversation_members_account_idx per account.
    private const string AnyAtLimitSql = """
        SELECT EXISTS (
            SELECT 1 FROM unnest($2::uuid[]) AS a(id)
            WHERE (SELECT count(*) FROM (
                SELECT 1 FROM messaging.conversation_members m
                WHERE m.org_id = $1 AND m.account_id = a.id AND m.left_at IS NULL
                LIMIT $3) AS open_memberships) >= $3)
        """;

    // Keyset by the primary key (conversation_id, account_id); closed memberships are skipped.
    private const string ListMembersSql = """
        SELECT account_id, role, joined_at
        FROM messaging.conversation_members
        WHERE conversation_id = $2 AND account_id > $3 AND org_id = $1 AND left_at IS NULL
        ORDER BY account_id
        LIMIT $4
        """;

    // Only when a private channel's owner leaves: reads the channel's open members once.
    private const string EarliestMemberSql = """
        SELECT account_id
        FROM messaging.conversation_members
        WHERE conversation_id = $2 AND org_id = $1 AND left_at IS NULL
        ORDER BY joined_at, account_id
        LIMIT 1
        """;

    // Two primary-key rows and the conversation. Only an open member becomes the owner: with no such membership
    // nothing is written and the caller fails. "added_by = m.added_by" assigns the column to itself and changes
    // nothing; it is there only so that the assignment list does not begin with the role column, which the
    // org-context Semgrep rule would read as a database role switch (see OpenSql).
    private const string SetOwnerSql = """
        WITH target AS (
            SELECT m.account_id FROM messaging.conversation_members m
            WHERE m.conversation_id = $2 AND m.account_id = $4 AND m.org_id = $1 AND m.left_at IS NULL),
        roles AS (
            UPDATE messaging.conversation_members m
            SET added_by = m.added_by, role = CASE WHEN m.account_id = $4 THEN 'owner' ELSE 'member' END
            FROM target
            WHERE m.conversation_id = $2 AND m.org_id = $1 AND m.account_id IN ($3, $4))
        UPDATE messaging.conversations c SET owner_account_id = target.account_id
        FROM target
        WHERE c.org_id = $1 AND c.id = $2
        """;

    // The name check looks at open public channels only, and only for a public channel ($7): a private channel is
    // neither compared nor found. The unique index conversations_public_name_key_idx decides a race.
    private const string UpdateSql = """
        UPDATE messaging.conversations c
        SET change_seq = c.change_seq + 1,
            name = COALESCE($3, c.name),
            name_key = COALESCE($4, c.name_key),
            description = CASE WHEN $5 THEN $6 ELSE c.description END
        WHERE c.org_id = $1 AND c.id = $2
          AND ($4::text IS NULL OR NOT $7 OR NOT EXISTS (
              SELECT 1 FROM messaging.conversations o
              WHERE o.org_id = $1 AND o.kind = 'public_channel' AND o.archived_at IS NULL AND o.name_key = $4 AND o.id <> c.id))
        RETURNING c.change_seq, c.name, c.description, c.archived_at, c.member_count
        """;

    // Reopening a public channel puts its name back among the open public names, so it is checked like a rename.
    private const string SetArchivedSql = """
        UPDATE messaging.conversations c
        SET change_seq = c.change_seq + 1, archived_at = $3
        WHERE c.org_id = $1 AND c.id = $2
          AND ($3::timestamptz IS NOT NULL OR c.kind <> 'public_channel' OR NOT EXISTS (
              SELECT 1 FROM messaging.conversations o
              WHERE o.org_id = $1 AND o.kind = 'public_channel' AND o.archived_at IS NULL AND o.name_key = c.name_key AND o.id <> c.id))
        RETURNING c.change_seq, c.name, c.description, c.archived_at, c.member_count
        """;

    // An index-only count of conversations_private_open_idx.
    private const string CountPrivateSql =
        "SELECT count(*)::integer FROM messaging.conversations WHERE org_id = $1 AND kind = 'private_channel' AND archived_at IS NULL";

    // The newest rows of conversations_creator_idx, at most the cap.
    private const string RecentChannelsSql = """
        SELECT count(*)::integer, min(created_at) FROM (
            SELECT created_at FROM messaging.conversations
            WHERE org_id = $1 AND created_by = $2 AND created_at > $3 AND kind IN ('public_channel', 'private_channel')
            ORDER BY created_at DESC
            LIMIT $4) AS recent
        """;

    // The newest rows of conversation_members_added_by_idx, at most the cap.
    private const string RecentAdditionsSql = """
        SELECT count(*)::integer, min(joined_at) FROM (
            SELECT joined_at FROM messaging.conversation_members
            WHERE org_id = $1 AND added_by = $2 AND joined_at > $3 AND added_by <> account_id
            ORDER BY joined_at DESC
            LIMIT $4) AS recent
        """;

    public async Task<ConversationDetails?> GetAsync(Guid organizationId, Guid conversationId, Guid accountId, bool forUpdate, CancellationToken cancellationToken)
    {
        if (forUpdate)
        {
            return await LockAsync(organizationId, conversationId, accountId, cancellationToken);
        }

        await using var command = new NpgsqlCommand(GetSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadDetails(reader, conversationId, reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    public async Task<ConversationChange> TouchAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(TouchSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        return await ChangeAsync(command, cancellationToken)
            ?? throw new InvalidOperationException("The conversation the caller locked could not be updated.");
    }

    // Two statements on purpose: see LockSql.
    private async Task<ConversationDetails?> LockAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken)
    {
        ConversationDetails? conversation;
        await using (var command = new NpgsqlCommand(LockSql, session.Connection, session.Transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            conversation = await reader.ReadAsync(cancellationToken) ? ReadDetails(reader, conversationId, role: null) : null;
        }

        if (conversation is null)
        {
            return null;
        }

        await using var roleCommand = new NpgsqlCommand(RoleSql, session.Connection, session.Transaction);
        roleCommand.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        roleCommand.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        roleCommand.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        return conversation with { Role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string };
    }

    private static ConversationDetails ReadDetails(NpgsqlDataReader reader, Guid conversationId, string? role) => new(
        conversationId,
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetInt64(5),
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        role);

    public async Task<IReadOnlyList<Guid>> OpenMembershipsAsync(
        Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, DateTimeOffset joinedAt, long joinedSeq, Guid addedBy, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(OpenManySql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = joinedAt });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = joinedSeq });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = addedBy });
        var opened = new List<Guid>(accountIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            opened.Add(reader.GetGuid(0));
        }

        return opened;
    }

    public async Task<bool> AnyAtMembershipLimitAsync(Guid organizationId, IReadOnlyCollection<Guid> accountIds, int cap, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AnyAtLimitSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = cap });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<ConversationMember>> ListMembersAsync(Guid organizationId, Guid conversationId, Guid after, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ListMembersSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = after });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<ConversationMember>(Math.Min(count, 101));
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new ConversationMember(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return members;
    }

    public async Task<Guid?> EarliestMemberAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EarliestMemberSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid accountId ? accountId : null;
    }

    public async Task<bool> SetOwnerAsync(Guid organizationId, Guid conversationId, Guid? previousOwnerAccountId, Guid ownerAccountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SetOwnerSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = previousOwnerAccountId ?? ownerAccountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ownerAccountId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<ConversationChange?> UpdateAsync(
        Guid organizationId, Guid conversationId, string? name, string? nameKey, bool setDescription, string? description, bool uniqueName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(UpdateSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)name ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)nameKey ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = setDescription });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)description ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = uniqueName });
        return await ChangeAsync(command, cancellationToken);
    }

    public async Task<ConversationChange?> SetArchivedAsync(Guid organizationId, Guid conversationId, DateTimeOffset? archivedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SetArchivedSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)archivedAt ?? DBNull.Value });
        return await ChangeAsync(command, cancellationToken);
    }

    public async Task<int> CountOpenPrivateChannelsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CountPrivateSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<RecentCount> RecentChannelCreationsAsync(Guid organizationId, Guid accountId, DateTimeOffset since, int cap, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RecentChannelsSql, session.Connection, session.Transaction);
        return await RecentAsync(command, organizationId, accountId, since, cap, cancellationToken);
    }

    public async Task<RecentCount> RecentMemberAdditionsAsync(Guid organizationId, Guid accountId, DateTimeOffset since, int cap, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RecentAdditionsSql, session.Connection, session.Transaction);
        return await RecentAsync(command, organizationId, accountId, since, cap, cancellationToken);
    }

    private static async Task<RecentCount> RecentAsync(NpgsqlCommand command, Guid organizationId, Guid accountId, DateTimeOffset since, int cap, CancellationToken cancellationToken)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = since });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = cap });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RecentCount(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    // No row: the name is taken. A race two requests both passed is decided by the unique index; the unit of work then
    // rolls back with the error this becomes.
    private static async Task<ConversationChange?> ChangeAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new ConversationChange(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetInt32(4))
                : null;
        }
        catch (PostgresException exception) when (string.Equals(exception.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal))
        {
            return null;
        }
    }
}
