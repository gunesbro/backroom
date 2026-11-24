using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Files.Application.Uploads;

namespace Backroom.Modules.Files.Infrastructure;

/// <summary>
/// Daily byte counters (one row per account and day, one per organization and day) and the minute count over
/// <c>files</c>; every statement is bounded by a primary key or the index <c>files_uploader_created_idx</c>.
/// </summary>
internal sealed class UploadLimitStore(DatabaseSession session) : IUploadLimitStore
{
    // A refused add still locks the row (ON CONFLICT ... WHERE), which keeps concurrent starts in order.
    private const string AddAccountBytesSql = """
        INSERT INTO files.account_upload_days AS d (org_id, account_id, day, bytes) VALUES ($1, $2, $3, $4)
        ON CONFLICT (org_id, account_id, day) DO UPDATE SET bytes = d.bytes + EXCLUDED.bytes
        WHERE d.bytes + EXCLUDED.bytes <= $5
        RETURNING bytes
        """;

    private const string AddOrganizationBytesSql = """
        INSERT INTO files.upload_days AS d (org_id, day, bytes) VALUES ($1, $2, $3)
        ON CONFLICT (org_id, day) DO UPDATE SET bytes = d.bytes + EXCLUDED.bytes
        WHERE d.bytes + EXCLUDED.bytes <= $4
        RETURNING bytes
        """;

    private const string RecentStartsSql = """
        SELECT created_at FROM files.files
        WHERE org_id = $1 AND uploader_account_id = $2 AND created_at > $3
        ORDER BY created_at DESC
        LIMIT $4
        """;

    public async Task<bool> TryAddAccountBytesAsync(Guid organizationId, Guid accountId, DateOnly day, long size, long limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AddAccountBytesSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateOnly> { TypedValue = day });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = size });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = limit });
        return await command.ExecuteScalarAsync(cancellationToken) is long;
    }

    public async Task<bool> TryAddOrganizationBytesAsync(Guid organizationId, DateOnly day, long size, long limit, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AddOrganizationBytesSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<DateOnly> { TypedValue = day });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = size });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = limit });
        return await command.ExecuteScalarAsync(cancellationToken) is long;
    }

    public async Task<IReadOnlyList<DateTimeOffset>> RecentStartsAsync(Guid organizationId, Guid accountId, DateTimeOffset since, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RecentStartsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = since });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var starts = new List<DateTimeOffset>(count);
        while (await reader.ReadAsync(cancellationToken))
        {
            starts.Add(reader.GetFieldValue<DateTimeOffset>(0));
        }

        return starts;
    }
}
