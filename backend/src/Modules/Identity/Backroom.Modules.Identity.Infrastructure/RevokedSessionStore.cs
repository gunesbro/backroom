using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Identity.Application;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Infrastructure;

/// <summary>Hand-written SQL in the scope's unit of work; every statement is bounded and uses the primary key or the <c>keep_until</c> index.</summary>
internal sealed class RevokedSessionStore(DatabaseSession session) : IRevokedSessionStore
{
    // A session id is unique, so a second logout of it changes nothing (no row, no message). A revocation of every
    // session of the account ('') is replaced by a later one.
    private const string RecordSql = """
        INSERT INTO identity.revoked_sessions AS r (session_id, account_id, revoked_at, keep_until)
        VALUES ($1, $2, $3, $4)
        ON CONFLICT (session_id, account_id) DO UPDATE
            SET revoked_at = EXCLUDED.revoked_at, keep_until = EXCLUDED.keep_until
            WHERE r.session_id = '' AND r.revoked_at < EXCLUDED.revoked_at
        """;

    // Newest first, in pages by keyset (revoked_at, account_id, session_id), so the caller can keep the newest revocations of one cell's
    // members however many other accounts have signed out.
    private const string ListSql = """
        SELECT session_id, account_id, revoked_at, keep_until
        FROM identity.revoked_sessions
        WHERE keep_until > $1
          AND ($3::timestamptz IS NULL OR (revoked_at, account_id, session_id) < ($3, $4, $5))
        ORDER BY revoked_at DESC, account_id DESC, session_id DESC
        LIMIT $2
        """;

    private const string DeleteSql = """
        DELETE FROM identity.revoked_sessions
        WHERE (session_id, account_id) IN (
            SELECT session_id, account_id FROM identity.revoked_sessions
            WHERE keep_until <= $1
            ORDER BY keep_until
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        """;

    public async Task<bool> TryRecordAsync(RevokedSession revocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        await using var command = new NpgsqlCommand(RecordSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = revocation.SessionId ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = revocation.AccountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = revocation.RevokedAt });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = revocation.KeepUntil });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<RevokedSession>> ListInForceAsync(DateTimeOffset now, int limit, RevokedSession? after, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ListSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = after?.RevokedAt });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = after?.AccountId ?? Guid.Empty });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = after?.SessionId ?? string.Empty });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var revocations = new List<RevokedSession>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var sessionId = reader.GetString(0);
            revocations.Add(new RevokedSession(
                reader.GetGuid(1),
                sessionId.Length == 0 ? null : sessionId,
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return revocations;
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(DeleteSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = batchSize });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
