using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Directory.Application.Invitations;

namespace Backroom.Modules.Directory.Infrastructure.Invitations;

/// <summary>
/// The control plane's invitations with hand-written SQL in the scope's unit of work. The table spans organizations
/// (no row-level security), so every statement names the organization the link checked.
/// </summary>
internal sealed class InvitationRegistry(DatabaseSession session) : IInvitationRegistry
{
    // Serializes the messages for one invitation id, whichever cell sends them; released at commit.
    private const string LockSql = "SELECT pg_advisory_xact_lock($1, hashtext($2::text))";

    /// <summary>The advisory lock class for invitation ids: "INVT" in ASCII.</summary>
    private const int InvitationLockClass = 0x494E5654;

    private const string FindSql = "SELECT org_id, status FROM directory.invitations WHERE invitation_id = $1";

    // By the unique index on code_hash; revocation tombstones have no hash and are never found.
    private const string FindByCodeSql = """
        SELECT i.invitation_id, i.org_id, o.name, o.status, i.status, i.expires_at, i.email_hmac
        FROM directory.invitations i
        JOIN directory.organizations o ON o.id = i.org_id
        WHERE i.code_hash = $1
        """;

    private const string CodeTakenSql = "SELECT EXISTS (SELECT 1 FROM directory.invitations WHERE code_hash = $1 AND invitation_id <> $2)";

    // A conflict here can only be the code hash: the advisory lock keeps the id to this transaction. Answering it
    // as a refusal instead of an error keeps a race between two cells from turning into a retry.
    private const string InsertSql = """
        INSERT INTO directory.invitations
            (invitation_id, org_id, cell_id, code_hash, email_hmac, role, expires_at, status, code_version, created_at, updated_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, 'pending', $8, $9, $9)
        ON CONFLICT DO NOTHING
        """;

    // Email, role and expiry stay as first registered: a resend changes only the code.
    private const string ReplaceCodeSql = """
        UPDATE directory.invitations
        SET code_hash = $3, code_version = $4, updated_at = $5
        WHERE invitation_id = $1 AND org_id = $2 AND status = 'pending' AND code_version < $4
        """;

    private const string TombstoneSql = """
        INSERT INTO directory.invitations (invitation_id, org_id, cell_id, status, revoked_at, created_at, updated_at)
        VALUES ($1, $2, $3, 'revoked', $4, $4, $4)
        """;

    private const string RevokeSql = """
        UPDATE directory.invitations SET status = 'revoked', revoked_at = $3, updated_at = $3
        WHERE invitation_id = $1 AND org_id = $2 AND status = 'pending'
        """;

    // While accepting, the cell's answer decides; only the first request is recorded.
    private const string RequestRevokeSql = """
        UPDATE directory.invitations SET revoke_requested_at = COALESCE(revoke_requested_at, $3), updated_at = $3
        WHERE invitation_id = $1 AND org_id = $2 AND status = 'accepting'
        """;

    private const string FindIdByCodeSql = "SELECT invitation_id FROM directory.invitations WHERE code_hash = $1";

    // Read again under the lock, by the same hash: a resend may have replaced the code meanwhile.
    private const string FindLockedSql = """
        SELECT i.invitation_id, i.org_id, o.status, o.cell_id, i.status, i.expires_at, i.email_hmac, i.accepting_account_id
        FROM directory.invitations i
        JOIN directory.organizations o ON o.id = i.org_id
        WHERE i.invitation_id = $1 AND i.code_hash = $2
        """;

    // Lock order for an acceptance: the invitation's advisory lock, its row, the membership row, then the outbox.
    // A membership that is pending or active is left as it is; a removed one (the person was a member before)
    // becomes pending again. The CTE always runs; the caller rolls back if the invitation was not pending.
    private const string StartAcceptanceSql = """
        WITH membership AS (
            INSERT INTO directory.memberships AS m (account_id, org_id, status, version, updated_at)
            VALUES ($3, $2, 'pending', 0, $4)
            ON CONFLICT (account_id, org_id) DO UPDATE SET status = 'pending', updated_at = EXCLUDED.updated_at
            WHERE m.status = 'removed'
            RETURNING 1)
        UPDATE directory.invitations
        SET status = 'accepting', accepting_account_id = $3, accepted_at = $4, updated_at = $4,
            added_membership = EXISTS (SELECT 1 FROM membership)
        WHERE invitation_id = $1 AND org_id = $2 AND status = 'pending'
        """;

    // Only for the cell the organization maps to; a requested revocation does not change the cell's answer. An
    // organization moved to another cell while an invitation is accepting leaves it accepting: the operations panel
    // raises acceptances older than 2 minutes. The cell has added the member, so a membership that the refusal of
    // another acceptance of the same account removed meanwhile becomes active too.
    private const string CompleteAcceptanceSql = """
        WITH invitation AS (
            UPDATE directory.invitations i SET status = 'used', used_at = $4, updated_at = $4
            FROM directory.organizations o
            WHERE i.invitation_id = $1 AND i.org_id = $2 AND i.status = 'accepting' AND o.id = i.org_id AND o.cell_id = $3
            RETURNING i.org_id, i.accepting_account_id),
        membership AS (
            UPDATE directory.memberships m SET status = 'active', updated_at = $4
            FROM invitation
            WHERE m.org_id = invitation.org_id AND m.account_id = invitation.accepting_account_id AND m.status IN ('pending', 'removed')
            RETURNING 1)
        SELECT accepting_account_id FROM invitation
        """;

    private const string FailAcceptanceSql = """
        WITH invitation AS (
            UPDATE directory.invitations i SET status = 'failed', failure_code = $4, updated_at = $5
            FROM directory.organizations o
            WHERE i.invitation_id = $1 AND i.org_id = $2 AND i.status = 'accepting' AND o.id = i.org_id AND o.cell_id = $3
            RETURNING i.org_id, i.accepting_account_id, i.added_membership),
        -- A pending membership is removed only if an acceptance of this account and organization made it pending (this
        -- one or an earlier one that left it for this one) and no other acceptance of theirs is still waiting for its cell.
        membership AS (
            UPDATE directory.memberships m SET status = 'removed', updated_at = $5
            FROM invitation
            WHERE m.org_id = invitation.org_id AND m.account_id = invitation.accepting_account_id AND m.status = 'pending'
              AND (invitation.added_membership OR EXISTS (
                  SELECT 1 FROM directory.invitations other
                  WHERE other.accepting_account_id = invitation.accepting_account_id AND other.org_id = invitation.org_id
                    AND other.added_membership))
              AND NOT EXISTS (
                  SELECT 1 FROM directory.invitations other
                  WHERE other.accepting_account_id = invitation.accepting_account_id AND other.org_id = invitation.org_id
                    AND other.status = 'accepting' AND other.invitation_id <> $1)
            RETURNING 1)
        SELECT accepting_account_id FROM invitation
        """;

    // By invitations_accepting_account_idx; only acceptances carry the account.
    private const string LatestAcceptanceSql = """
        SELECT org_id, status, failure_code FROM directory.invitations
        WHERE accepting_account_id = $1
        ORDER BY accepted_at DESC, invitation_id DESC
        LIMIT 1
        """;

    // The jobs (flow 3, step 8, and "Privacy and retention") never wait for a lock: they skip rows another
    // transaction has locked and invitations whose advisory lock a message or an acceptance holds. So they keep the
    // lock order of those paths (invitation lock, then row) without ever blocking them, and several instances share
    // the work. A skipped invitation is picked up by a later batch or run. The materialized CTEs bound the advisory
    // locks tried to the batch. Each statement also answers how many rows it selected, so the job can tell a batch
    // that found nothing more from one that skipped busy rows.

    // By invitations_pending_expiry_idx. The control plane expires at expires_at, where acceptance stops too.
    private const string ExpireDueSql = """
        WITH due AS MATERIALIZED (
            SELECT invitation_id FROM directory.invitations
            WHERE status = 'pending' AND expires_at <= $1
            ORDER BY expires_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED),
        free AS MATERIALIZED (
            SELECT invitation_id FROM due WHERE pg_try_advisory_xact_lock($3, hashtext(invitation_id::text))),
        expired AS (
            UPDATE directory.invitations i SET status = 'expired', updated_at = $1
            FROM free
            WHERE i.invitation_id = free.invitation_id AND i.status = 'pending'
            RETURNING i.invitation_id, i.org_id)
        SELECT selected.count, expired.invitation_id, expired.org_id
        FROM (SELECT count(*)::integer AS count FROM due) selected
        LEFT JOIN expired ON true
        """;

    // By invitations_ended_email_idx. Nothing changes an ended invitation, so its updated_at is when it ended
    // (expired, revoked or failed) or was used; clearing the HMAC takes the row out of the index. A used invitation
    // needs it no more than an ended one: a repeat by the accepting account is answered by the account id.
    private const string ClearEndedEmailSql = """
        WITH ended AS MATERIALIZED (
            SELECT invitation_id FROM directory.invitations
            WHERE email_hmac IS NOT NULL AND status IN ('expired', 'revoked', 'failed', 'used') AND updated_at < $1
            ORDER BY updated_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED),
        free AS MATERIALIZED (
            SELECT invitation_id FROM ended WHERE pg_try_advisory_xact_lock($3, hashtext(invitation_id::text))),
        cleared AS (
            UPDATE directory.invitations i SET email_hmac = NULL, updated_at = $4
            FROM free
            WHERE i.invitation_id = free.invitation_id AND i.status IN ('expired', 'revoked', 'failed', 'used')
            RETURNING 1)
        SELECT (SELECT count(*)::integer FROM ended), (SELECT count(*)::integer FROM cleared)
        """;

    // By invitations_tombstone_idx. Only a revocation of an unknown invitation leaves a row without a code version.
    private const string DeleteTombstonesSql = """
        WITH old AS MATERIALIZED (
            SELECT invitation_id FROM directory.invitations
            WHERE status = 'revoked' AND code_version IS NULL AND revoked_at < $1
            ORDER BY revoked_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED),
        free AS MATERIALIZED (
            SELECT invitation_id FROM old WHERE pg_try_advisory_xact_lock($3, hashtext(invitation_id::text))),
        deleted AS (
            DELETE FROM directory.invitations i
            USING free
            WHERE i.invitation_id = free.invitation_id AND i.status = 'revoked' AND i.code_version IS NULL
            RETURNING 1)
        SELECT (SELECT count(*)::integer FROM old), (SELECT count(*)::integer FROM deleted)
        """;

    public async Task<RegistryOutcome> RegisterAsync(InvitationRegistration registration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await LockAsync(registration.InvitationId, cancellationToken);
        var existing = await FindAsync(registration.InvitationId, cancellationToken);
        if (existing is { } row && row.OrganizationId != registration.OrganizationId)
        {
            return RegistryOutcome.OtherOrganization;
        }

        if (await IsCodeTakenAsync(registration, cancellationToken))
        {
            return RegistryOutcome.CodeTaken;
        }

        if (existing is null)
        {
            await using var insert = new NpgsqlCommand(InsertSql, session.Connection, session.Transaction);
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = registration.InvitationId });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = registration.OrganizationId });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = registration.CellId });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = registration.CodeHash });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = registration.EmailHmac });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = registration.Role });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = registration.ExpiresAt });
            insert.Parameters.Add(new NpgsqlParameter<int> { TypedValue = registration.CodeVersion });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            return await insert.ExecuteNonQueryAsync(cancellationToken) == 1 ? RegistryOutcome.Applied : RegistryOutcome.CodeTaken;
        }

        // A hash taken by a concurrent registration of another cell between the check and here fails this update
        // once; the retry then finds it taken. Cells derive codes from their own keys, so this does not happen in
        // practice.
        await using var replace = new NpgsqlCommand(ReplaceCodeSql, session.Connection, session.Transaction);
        replace.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = registration.InvitationId });
        replace.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = registration.OrganizationId });
        replace.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = registration.CodeHash });
        replace.Parameters.Add(new NpgsqlParameter<int> { TypedValue = registration.CodeVersion });
        replace.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await replace.ExecuteNonQueryAsync(cancellationToken) == 1 ? RegistryOutcome.Applied : RegistryOutcome.Ignored;
    }

    public async Task<RegistryOutcome> RevokeAsync(Guid organizationId, Guid invitationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockAsync(invitationId, cancellationToken);
        var existing = await FindAsync(invitationId, cancellationToken);
        if (existing is null)
        {
            await using var tombstone = new NpgsqlCommand(TombstoneSql, session.Connection, session.Transaction);
            tombstone.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
            tombstone.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
            tombstone.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
            tombstone.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            await tombstone.ExecuteNonQueryAsync(cancellationToken);
            return RegistryOutcome.Applied;
        }

        if (existing.Value.OrganizationId != organizationId)
        {
            return RegistryOutcome.OtherOrganization;
        }

        switch (existing.Value.Status)
        {
            case "pending":
                await using (var revoke = new NpgsqlCommand(RevokeSql, session.Connection, session.Transaction))
                {
                    return await ExecuteForAsync(revoke, invitationId, organizationId, now, cancellationToken) ? RegistryOutcome.Applied : RegistryOutcome.Ignored;
                }

            case "accepting":
                await using (var request = new NpgsqlCommand(RequestRevokeSql, session.Connection, session.Transaction))
                {
                    return await ExecuteForAsync(request, invitationId, organizationId, now, cancellationToken) ? RegistryOutcome.RevokeRequested : RegistryOutcome.Ignored;
                }

            default:
                return RegistryOutcome.Ignored;
        }
    }

    private static async Task<bool> ExecuteForAsync(NpgsqlCommand command, Guid invitationId, Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<InvitationByCode?> FindByCodeAsync(byte[] codeHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindByCodeSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvitationByCode(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : await reader.GetFieldValueAsync<byte[]>(6, cancellationToken));
    }

    public async Task<InvitationForAcceptance?> LockByCodeAsync(byte[] codeHash, CancellationToken cancellationToken)
    {
        Guid invitationId;
        await using (var find = new NpgsqlCommand(FindIdByCodeSql, session.Connection, session.Transaction))
        {
            find.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
            if (await find.ExecuteScalarAsync(cancellationToken) is not Guid id)
            {
                return null;
            }

            invitationId = id;
        }

        await LockAsync(invitationId, cancellationToken);
        await using var command = new NpgsqlCommand(FindLockedSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvitationForAcceptance(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            await reader.IsDBNullAsync(5, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : await reader.GetFieldValueAsync<byte[]>(6, cancellationToken),
            await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetGuid(7));
    }

    public async Task<bool> StartAcceptanceAsync(Guid invitationId, Guid organizationId, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(StartAcceptanceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<Guid?> CompleteAcceptanceAsync(Guid organizationId, Guid invitationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockAsync(invitationId, cancellationToken);
        await using var command = new NpgsqlCommand(CompleteAcceptanceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid accountId ? accountId : null;
    }

    public async Task<Guid?> FailAcceptanceAsync(Guid organizationId, Guid invitationId, string cellId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockAsync(invitationId, cancellationToken);
        await using var command = new NpgsqlCommand(FailAcceptanceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteScalarAsync(cancellationToken) is Guid accountId ? accountId : null;
    }

    public async Task<InvitationAcceptanceState?> FindLatestAcceptanceAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LatestAcceptanceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvitationAcceptanceState(
            reader.GetGuid(0),
            reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2));
    }

    public async Task<InvitationJobBatch> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ExpireDueSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = InvitationLockClass });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var selected = 0;
        var expired = new List<ExpiredInvitation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            selected = reader.GetInt32(0);
            if (!await reader.IsDBNullAsync(1, cancellationToken))
            {
                expired.Add(new ExpiredInvitation(reader.GetGuid(1), reader.GetGuid(2)));
            }
        }

        return new InvitationJobBatch(selected, expired.Count, expired);
    }

    public async Task<InvitationJobBatch> ClearEndedEmailHmacsAsync(DateTimeOffset endedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ClearEndedEmailSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = endedBefore });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = InvitationLockClass });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await ReadCountsAsync(command, cancellationToken);
    }

    public async Task<InvitationJobBatch> DeleteTombstonesAsync(DateTimeOffset revokedBefore, int limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DeleteTombstonesSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = revokedBefore });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = InvitationLockClass });
        return await ReadCountsAsync(command, cancellationToken);
    }

    private static async Task<InvitationJobBatch> ReadCountsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return InvitationJobBatch.Counted(reader.GetInt32(0), reader.GetInt32(1));
    }

    private async Task LockAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LockSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = InvitationLockClass });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<(Guid OrganizationId, string Status)?> FindAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FindSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1)) : null;
    }

    private async Task<bool> IsCodeTakenAsync(InvitationRegistration registration, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CodeTakenSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = registration.CodeHash });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = registration.InvitationId });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
