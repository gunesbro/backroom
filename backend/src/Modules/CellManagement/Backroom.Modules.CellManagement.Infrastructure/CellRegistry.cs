using Npgsql;
using Backroom.Modules.CellManagement.Application.Listing;
using Backroom.Modules.CellManagement.Application.Registration;

namespace Backroom.Modules.CellManagement.Infrastructure;

/// <summary>The registry's SQL; each method is one statement or one short transaction.</summary>
internal sealed class CellRegistry(NpgsqlDataSource dataSource, TimeProvider timeProvider) : ICellRegistry
{
    // A key can be claimed while unused and unexpired, or again by the same public key after it was used:
    // that retry (for example after a lost response) finds everything already done and succeeds again.
    private const string ClaimSql = """
        UPDATE cell_management.registration_keys
        SET claimed_key_thumbprint = $2, claimed_at = coalesce(claimed_at, $3)
        WHERE key_hash = $1
          AND ((used_at IS NULL AND expires_at > $3 AND (claimed_key_thumbprint IS NULL OR claimed_key_thumbprint = $2))
               OR (used_at IS NOT NULL AND claimed_key_thumbprint = $2))
        RETURNING cell_id
        """;

    private const string CompleteSql = """
        WITH used AS (
            UPDATE cell_management.registration_keys
            SET used_at = $3
            WHERE key_hash = $1 AND claimed_key_thumbprint = $2 AND used_at IS NULL
            RETURNING cell_id)
        UPDATE cell_management.cells
        SET status = 'registered', registered_at = $3, version = $4
        WHERE id IN (SELECT cell_id FROM used)
        """;

    private const string AddCellSql = """
        WITH cell AS (
            INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at)
            VALUES ($1, $2, $3, $4, $5, 'pending', $6)
            ON CONFLICT (id) DO NOTHING
            RETURNING id)
        INSERT INTO cell_management.registration_keys (key_hash, cell_id, expires_at)
        SELECT $7, id, $8 FROM cell
        """;

    // A pending cell holds a slot only while its key can still be used; an abandoned one gives the slot back.
    private const string CountSharedSql = """
        SELECT count(*)::int FROM cell_management.cells c
        WHERE c.type = 'shared'
          AND (c.status = 'registered'
               OR EXISTS (SELECT 1 FROM cell_management.registration_keys k
                          WHERE k.cell_id = c.id AND k.used_at IS NULL AND k.expires_at > $1))
        """;

    private const string SharedIdsSql = """
        SELECT id FROM cell_management.cells
        WHERE type = 'shared' AND status = 'registered'
        ORDER BY registered_at, id
        LIMIT $1
        """;

    // Development: the cell exists, and while it is pending its key (from the AppHost) is valid again.
    private const string SeedSql = """
        WITH cell AS (
            INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at)
            VALUES ($1, $2, $3, $4, $5, 'pending', $6)
            ON CONFLICT (id) DO UPDATE SET id = EXCLUDED.id
            RETURNING id, status)
        INSERT INTO cell_management.registration_keys (key_hash, cell_id, expires_at)
        SELECT $7, id, $8 FROM cell WHERE status = 'pending'
        ON CONFLICT (key_hash) DO UPDATE SET expires_at = EXCLUDED.expires_at
        WHERE registration_keys.used_at IS NULL AND registration_keys.cell_id = EXCLUDED.cell_id
        """;

    // Counts are capped (10001 means "more than 10000") and use outbound_messages_pending_idx and
    // outbound_messages_rejected_idx. Two statements, so that the cursor is a primary-key range scan.
    private const string ListColumns = """
        SELECT c.id, c.type, c.region, c.status, c.version, c.last_heartbeat_at,
               (SELECT count(*)::int FROM (SELECT 1 FROM cell_management.outbound_messages m
                WHERE m.cell_id = c.id AND m.acked_at IS NULL AND m.rejected_code IS NULL LIMIT 10001) p),
               (SELECT count(*)::int FROM (SELECT 1 FROM cell_management.outbound_messages m
                WHERE m.cell_id = c.id AND m.rejected_code IS NOT NULL LIMIT 10001) r)
        FROM cell_management.cells c
        """;

    private const string ListFirstSql = ListColumns + "\nORDER BY c.id LIMIT $1";

    private const string ListAfterSql = ListColumns + "\nWHERE c.id > $1 ORDER BY c.id LIMIT $2";

    public async Task<IReadOnlyList<CellRow>> ListCellsAsync(string? after, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(after is null ? ListFirstSql : ListAfterSql);
        if (after is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = after });
        }

        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CellRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CellRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }

        return rows;
    }

    public async Task SeedDevelopmentCellAsync(NewCell cell, byte[] keyHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cell);
        await using var command = dataSource.CreateCommand(SeedSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Name });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Type });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Region });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.PublicUrl.ToString() });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = keyHash });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> ClaimRegistrationKeyAsync(byte[] keyHash, string thumbprint, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ClaimSql);
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = keyHash });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = thumbprint });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task CompleteRegistrationAsync(byte[] keyHash, string thumbprint, string version, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(CompleteSql);
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = keyHash });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = thumbprint });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsRegisteredAsync(string cellId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM cell_management.cells WHERE id = $1 AND status = 'registered')");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task RecordHeartbeatAsync(string cellId, string version, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE cell_management.cells SET last_heartbeat_at = $2, version = $3 WHERE id = $1 AND status = 'registered'");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = version });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountSharedCellsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(CountSharedSql);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<string>> SharedCellIdsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SharedIdsSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task<bool> AddCellAsync(NewCell cell, byte[] keyHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cell);
        await using var command = dataSource.CreateCommand(AddCellSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Id });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Name });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Type });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.Region });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cell.PublicUrl.ToString() });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = keyHash });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
