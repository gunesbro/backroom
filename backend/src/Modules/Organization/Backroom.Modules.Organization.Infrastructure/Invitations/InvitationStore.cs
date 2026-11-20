using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Organization.Application.Invitations;

namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>Invitations with hand-written SQL in the scope's unit of work; row-level security limits every statement to the entered organization.</summary>
internal sealed class InvitationStore(DatabaseSession session) : IInvitationStore
{
    // A request waiting longer than this fails instead of holding a pooled connection; the setting ends with the transaction.
    private const string LockTimeoutSql = "SET LOCAL lock_timeout = '5s'";

    // NO KEY UPDATE: waits for other invitation creations but not for inserts that reference the organization.
    private const string LockSql = "SELECT 1 FROM organization.organizations WHERE id = $1 FOR NO KEY UPDATE";

    // email_key is the stored email in ASCII lower case (a generated column); '=' can use its index under
    // row-level security, which lower() in the query could not. 'active' sorts before 'deactivated'.
    private const string MemberStatusSql = """
        SELECT status FROM organization.members
        WHERE email_key = $1 AND status IN ('active', 'deactivated')
        ORDER BY status
        LIMIT 1
        """;

    private const string PendingInviterSql = "SELECT created_by FROM organization.invitations WHERE status = 'pending' AND email = $1";

    // Sends in the window: new invitations and resends, each with its sender and recipient.
    private const string CountRecentSql = """
        WITH sends AS (
            SELECT created_by AS sender, email, created_at AS sent_at
            FROM organization.invitations
            WHERE created_at > $3
            UNION ALL
            SELECT r.resent_by, i.email, r.resent_at
            FROM organization.invitation_resends r
            JOIN organization.invitations i ON i.org_id = r.org_id AND i.id = r.invitation_id
            WHERE r.resent_at > $3)
        SELECT count(*) FILTER (WHERE sender = $1), min(sent_at) FILTER (WHERE sender = $1),
               count(*), min(sent_at),
               count(*) FILTER (WHERE email = $2), min(sent_at) FILTER (WHERE email = $2)
        FROM sends
        """;

    private const string GetSql = """
        SELECT id, email, role, status, created_by, created_at, expires_at, code_version
        FROM organization.invitations
        WHERE id = $1
        """;

    private const string RevokeSql = """
        UPDATE organization.invitations
        SET status = 'revoked', revoked_at = $3, revoked_by = $2
        WHERE id = $1 AND status = 'pending'
        """;

    // registered_at is cleared: the new code is not registered with the control plane yet. The version must be the
    // one just read, so two re-issues can never produce the same version even without the organization lock.
    private const string ReissueSql = """
        WITH reissued AS (
            UPDATE organization.invitations
            SET salt = $2, key_version = $3, code_hash = $4, code_version = $5, registered_at = NULL
            WHERE id = $1 AND status = 'pending' AND code_version = $5 - 1
            RETURNING org_id, id)
        INSERT INTO organization.invitation_resends (id, org_id, invitation_id, resent_by, resent_at)
        SELECT $6, org_id, id, $7, $8 FROM reissued
        """;

    private const string CountPendingExceptSql = "SELECT count(*) FROM organization.invitations WHERE status = 'pending' AND email <> $1";

    private const string RevokePendingSql = """
        UPDATE organization.invitations
        SET status = 'revoked', revoked_at = $3, revoked_by = $2
        WHERE status = 'pending' AND email = $1
        RETURNING id
        """;

    private const string AddSql = """
        INSERT INTO organization.invitations
            (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at)
        VALUES ($1, $2, $3, $4, $5, $6, 1, $7, 'pending', $8, $9, $10)
        """;

    private const string PendingCodeHashSql = """
        SELECT code_hash FROM organization.invitations
        WHERE id = $1 AND status = 'pending' AND code_version = $2 AND expires_at > $3
        """;

    // registered_at belongs to the current code version (a re-issue clears it), so the link already resolves upstream.
    // One primary-key lookup; row-level security applies to both tables.
    private const string LinkSourceSql = """
        SELECT i.email, o.name, i.salt, i.key_version, i.code_hash
        FROM organization.invitations i
        JOIN organization.organizations o ON o.id = i.org_id
        WHERE i.id = $1 AND i.status = 'pending' AND i.code_version = $2 AND i.registered_at IS NOT NULL
          AND i.expires_at > $3 AND i.email IS NOT NULL
        """;

    // Only the first acknowledgement of a version records it, so the email is triggered once.
    private const string MarkRegisteredSql = """
        UPDATE organization.invitations
        SET registered_at = $3
        WHERE id = $1 AND status = 'pending' AND code_version = $2 AND registered_at IS NULL AND expires_at > $3
        """;

    // The conditional update that decides the acceptance; a revoke or resend either committed before or waits.
    private const string MarkAcceptedSql = """
        UPDATE organization.invitations
        SET status = 'accepted', accepted_at = $3, accepted_by = $2
        WHERE id = $1 AND status = 'pending'
        """;

    private const string ListPendingSql = """
        SELECT id, email, role, created_by, created_at, expires_at
        FROM organization.invitations
        WHERE status = 'pending' AND id > $1 AND ($2::uuid IS NULL OR created_by = $2)
        ORDER BY id
        LIMIT $3
        """;

    public async Task LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using (var timeout = new NpgsqlCommand(LockTimeoutSql, session.Connection, session.Transaction))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(LockSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });

        // No row means nothing was locked, and concurrent creations would not wait for each other.
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("The organization row could not be locked in the current unit of work.");
        }
    }

    public async Task<Guid?> FindPendingInviterAsync(string email, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PendingInviterSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = email });
        return (Guid?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<string?> FindMemberStatusAsync(string email, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MemberStatusSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = email });
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<RecentInvitations> CountRecentAsync(Guid inviterAccountId, string email, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CountRecentSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = inviterAccountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = email });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = since });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RecentInvitations(
            checked((int)reader.GetInt64(0)),
            await ReadTimeAsync(reader, 1, cancellationToken),
            checked((int)reader.GetInt64(2)),
            await ReadTimeAsync(reader, 3, cancellationToken),
            checked((int)reader.GetInt64(4)),
            await ReadTimeAsync(reader, 5, cancellationToken));
    }

    public async Task<int> CountPendingExceptAsync(string email, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CountPendingExceptSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = email });
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    public async Task<IReadOnlyList<Guid>> RevokePendingAsync(string email, Guid revokedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RevokePendingSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = email });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = revokedBy });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var revoked = new List<Guid>(1);
        while (await reader.ReadAsync(cancellationToken))
        {
            revoked.Add(reader.GetGuid(0));
        }

        return revoked;
    }

    public async Task AddAsync(NewInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        await using var command = new NpgsqlCommand(AddSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation.Id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation.OrganizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = invitation.Email });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = invitation.Role });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = invitation.Code.Salt });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = invitation.Code.KeyVersion });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = invitation.Code.CodeHash });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation.CreatedBy });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = invitation.CreatedAt });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = invitation.ExpiresAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<InvitationState?> GetAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(GetSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvitationState(
            reader.GetGuid(0),
            await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetGuid(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7));
    }

    public async Task<bool> RevokeAsync(Guid invitationId, Guid revokedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RevokeSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = revokedBy });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> ReissueAsync(Guid invitationId, IssuedInvitationCode code, int codeVersion, Guid resentBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        await using var command = new NpgsqlCommand(ReissueSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = code.Salt });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = code.KeyVersion });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = code.CodeHash });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = codeVersion });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7(now) });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = resentBy });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<byte[]?> GetPendingCodeHashAsync(Guid invitationId, int codeVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PendingCodeHashSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = codeVersion });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return (byte[]?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<InvitationLinkSource?> GetLinkSourceAsync(Guid invitationId, int codeVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LinkSourceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = codeVersion });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvitationLinkSource(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetFieldValue<byte[]>(2),
            reader.GetInt32(3),
            reader.GetFieldValue<byte[]>(4));
    }

    public async Task<bool> MarkRegisteredAsync(Guid invitationId, int codeVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarkRegisteredSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = codeVersion });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> MarkAcceptedAsync(Guid invitationId, Guid acceptedBy, DateTimeOffset acceptedAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarkAcceptedSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = acceptedBy });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = acceptedAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<InvitationEntry>> ListPendingAsync(Guid? createdBy, Guid after, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ListPendingSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = after });
        command.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = createdBy });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invitations = new List<InvitationEntry>(count);
        while (await reader.ReadAsync(cancellationToken))
        {
            invitations.Add(new InvitationEntry(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return invitations;
    }

    private static async Task<DateTimeOffset?> ReadTimeAsync(NpgsqlDataReader reader, int ordinal, CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
