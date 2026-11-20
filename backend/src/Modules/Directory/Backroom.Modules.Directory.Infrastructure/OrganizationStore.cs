using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Directory.Application.Organizations;

namespace Backroom.Modules.Directory.Infrastructure;

/// <summary>Directory's organization writes with hand-written SQL, in the scope's unit of work.</summary>
internal sealed class OrganizationStore(DatabaseSession session) : IOrganizationStore
{
    /// <summary>The advisory lock class for organization creation: "ORGC" in ASCII; the key is the account.</summary>
    private const int CreationLockClass = 0x4F524743;

    private const string LockSql = "SELECT pg_try_advisory_xact_lock($1, hashtext($2::text))";

    private const string CountSql = "SELECT count(*), min(created_at) FROM directory.organizations WHERE created_by = $1 AND created_at > $2";

    private const string CountByCellSql = """
        SELECT cell_id, count(*)::int FROM directory.organizations
        WHERE cell_id = ANY($1) AND status <> 'failed'
        GROUP BY cell_id
        """;

    private const string AddOrganizationSql = """
        INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at)
        VALUES ($1, $2, $3, $4, 'provisioning', $5, $6)
        """;

    private const string AddOwnerSql = """
        INSERT INTO directory.memberships (account_id, org_id, status, version, updated_at)
        VALUES ($1, $2, 'pending', 0, $3)
        """;

    // The owner's membership keeps version 0: versions come from the cell's MembershipChanged.
    private const string ActivateSql = """
        WITH organization AS (
            UPDATE directory.organizations SET status = 'active'
            WHERE id = $1 AND cell_id = $2 AND status = 'provisioning'
            RETURNING id, created_by)
        UPDATE directory.memberships m SET status = 'active', updated_at = $3
        FROM organization
        WHERE m.org_id = organization.id AND m.account_id = organization.created_by AND m.status = 'pending'
        """;

    private const string FailSql = """
        WITH organization AS (
            UPDATE directory.organizations SET status = 'failed', failure_code = $3
            WHERE id = $1 AND cell_id = $2 AND status = 'provisioning'
            RETURNING id, created_by)
        UPDATE directory.memberships m SET status = 'removed', updated_at = $4
        FROM organization
        WHERE m.org_id = organization.id AND m.account_id = organization.created_by AND m.status = 'pending'
        """;

    public async Task<(int Count, DateTimeOffset? Oldest)?> TryLockAndCountCreatedSinceAsync(
        Guid accountId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using (var lockCommand = new NpgsqlCommand(LockSql, session.Connection, session.Transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter<int> { TypedValue = CreationLockClass });
            lockCommand.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return null;
            }
        }

        await using var command = new NpgsqlCommand(CountSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = since });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var oldest = await reader.IsDBNullAsync(1, cancellationToken) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        return ((int)reader.GetInt64(0), oldest);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByCellAsync(IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CountByCellSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. cellIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    public async Task AddProvisioningAsync(
        Guid organizationId, string name, string region, string cellId, Guid ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(AddOrganizationSql, session.Connection, session.Transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = region });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ownerId });
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var owner = new NpgsqlCommand(AddOwnerSql, session.Connection, session.Transaction);
        owner.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ownerId });
        owner.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        owner.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await owner.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ActivateAsync(Guid organizationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ActivateSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> FailAsync(Guid organizationId, string cellId, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(FailSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
